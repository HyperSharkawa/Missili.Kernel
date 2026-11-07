using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Agents;

namespace Missili.Kernel;

/// <summary>顺序变换、最终审批与执行前资格检查；实际平台提交仍由 MessageSender 独占。</summary>
public sealed class OutgoingPipeline
{
    private static readonly AsyncLocal<int> Processing = new();
    private static readonly JsonElement EmptyConfiguration = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
    private readonly IConversationProfileService _profiles;
    private readonly IOutgoingProcessorRuntime _processors;
    private readonly IToolApprovalService _approvals;
    private readonly IOutgoingStore _store;
    private readonly AgentAuthorization _authorization;
    private readonly IInvocationScheduler _scheduler;
    private readonly int _perPlugin;
    private readonly TimeProvider _clock;

    /// <summary>组合已有共享服务，处理器和审批服务不拥有独立发送通道。</summary>
    public OutgoingPipeline(IConversationProfileService profiles, IOutgoingProcessorRuntime processors, IToolApprovalService approvals,
        IOutgoingStore store, AgentAuthorization authorization, IInvocationScheduler scheduler, int perPlugin, TimeProvider clock)
    { _profiles = profiles; _processors = processors; _approvals = approvals; _store = store; _authorization = authorization; _scheduler = scheduler; _perPlugin = perPlugin; _clock = clock; }

    internal static bool IsProcessing => Processing.Value > 0;

    /// <summary>一个批次固定配置及处理器代次，所有处理成功后才冻结实际发送内容。</summary>
    internal async Task<PreparedOutgoing> PrepareAsync(OutgoingBatch batch, CancellationToken token)
    {
        ConversationProfileSnapshot? profile = _profiles.TryResolve(batch.Address);
        OutgoingOptions options = profile?.Profile.Outgoing ?? new OutgoingOptions();
        ImmutableArray<MessageDraft> messages = batch.Messages;
        ImmutableArray<OutgoingProcessorDescriptor>.Builder applied = ImmutableArray.CreateBuilder<OutgoingProcessorDescriptor>();
        Dictionary<HandlerKey, OutgoingProcessorDescriptor> available = _processors.CaptureProcessors().ToDictionary(item => item.Key);
        ImmutableArray<OutgoingProcessorDescriptor>.Builder selected = ImmutableArray.CreateBuilder<OutgoingProcessorDescriptor>();
        OutgoingProcessingSnapshot snapshot = new()
        {
            ProfileId = profile?.Profile.Id,
            ConfigurationRevision = profile?.Revision,
            Options = options,
            SelectedProcessors = options.Processors.Where(available.ContainsKey).Select(key => available[key]).ToImmutableArray(),
            PluginConfigurations = options.Processors.Select(key => key.PluginId).Distinct(StringComparer.Ordinal).ToImmutableDictionary(id => id,
                id => profile is not null && profile.Profile.Plugins.TryGetValue(id, out JsonElement settings) ? settings.Clone() : EmptyConfiguration, StringComparer.Ordinal)
        };
        await _store.SetBatchProcessingAsync(batch.Id, snapshot, token).ConfigureAwait(false);
        try
        {
            Validate(messages, options);
            foreach (HandlerKey key in options.Processors)
            {
                if (!available.TryGetValue(key, out OutgoingProcessorDescriptor? descriptor) || !descriptor.Enabled)
                    return Failed(batch, messages.Length, "ProcessorUnavailable", "配置要求的出站处理器当前不可用：" + key.PluginId + "/" + key.HandlerId);
                selected.Add(descriptor);
            }
            foreach (OutgoingProcessorDescriptor descriptor in selected)
            {
                using IOutgoingProcessorLease? lease = _processors.TryAcquireProcessor(descriptor);
                if (lease is null) return Failed(batch, messages.Length, "ProcessorUnavailable", "本批处理器代次已撤销。");
                using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
                JsonElement configuration = profile is not null && profile.Profile.Plugins.TryGetValue(descriptor.Key.PluginId, out JsonElement settings) ? settings : EmptyConfiguration;
                OutgoingProcessingContext context = new() { Original = batch, Messages = messages, Profile = profile, Configuration = configuration };
                Task<OutgoingProcessingResult> work = await _scheduler.EnqueueAsync(descriptor.Key.PluginId, Math.Min(_perPlugin, descriptor.MaxConcurrency),
                    callToken => InvokeProcessorAsync(lease, context, options.ProcessorTimeout, callToken), lifetime.Token).ConfigureAwait(false);
                OutgoingProcessingResult result = await work.ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                if (result.Error is not null) return Failed(batch, messages.Length, "Processing", result.Error);
                applied.Add(descriptor);
                if (result.Filtered)
                {
                    if (!result.Messages.IsEmpty) return Failed(batch, messages.Length, "Processing", "过滤结果不能同时返回待发送消息。");
                    await _store.SetBatchContentAsync(batch.Id, [], applied.ToImmutable(), token).ConfigureAwait(false);
                    return new PreparedOutgoing
                    {
                        Messages = [],
                        Failure = new BatchSendResult
                        { BatchId = batch.Id, State = BatchSendState.Filtered, TotalMessages = 0, Recorded = true, Error = result.Reason }
                    };
                }
                messages = result.Messages.Select(message => message.Freeze()).ToImmutableArray();
                Validate(messages, options);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed(batch, messages.Length, "ProcessorUnavailable", "处理器代次停止，本批没有绕过处理发送原文。"); }
        catch (Exception exception) when (exception is ArgumentException or TimeoutException or InvalidOperationException)
        { return Failed(batch, messages.Length, "Processing", "出站内容处理失败：" + exception.GetType().Name); }

        BatchSendState? eligibility = await CanSendAsync(batch, token).ConfigureAwait(false);
        if (eligibility.HasValue) return Rejected(batch, messages.Length, eligibility.Value, "任务已停止、发送资格已撤销或目标不属于当前任务。");
        try { await _store.SetBatchContentAsync(batch.Id, messages, applied.ToImmutable(), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failed(batch, messages.Length, "Storage", "最终内容记录失败：" + exception.GetType().Name); }
        ToolApprovalRequest? approvalRequest = null;
        ToolApprovalResult approval = new() { State = ToolApprovalState.NotRequired };
        if (profile is not null)
        {
            approvalRequest = new ToolApprovalRequest
            {
                ToolId = ApprovalActionIds.MessageSend,
                Description = "向固定会话发送已经完成后处理的消息批次。",
                Conversation = batch.Address,
                PermissionCategory = "message.send",
                HasSideEffects = true,
                Origin = batch.Origin,
                Profile = profile,
                Prepared = new PreparedToolCall
                {
                    Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["address"] = batch.Address, ["messages"] = messages }),
                    ExecutionDescription = "逐条保存意图并发送；失败或未确认时停止剩余部分，不自动重发。"
                },
                Evidence = await _authorization.EvidenceAsync(batch.Address, batch.Origin, batch.TriggerMessageId, profile, token).ConfigureAwait(false)
            };
            approval = await _approvals.ReviewAsync(approvalRequest, token).ConfigureAwait(false);
        }
        try { await _store.SetBatchApprovalAsync(batch.Id, approvalRequest, approval, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failed(batch, messages.Length, "Storage", "发送审批记录失败：" + exception.GetType().Name); }
        if (!approval.Permitted)
            return new PreparedOutgoing
            {
                Messages = messages,
                ApprovalRequest = approvalRequest,
                Failure = new BatchSendResult
                {
                    BatchId = batch.Id,
                    TotalMessages = messages.Length,
                    Recorded = true,
                    State = approval.State == ToolApprovalState.Denied ? BatchSendState.Rejected : approval.State == ToolApprovalState.Cancelled ? BatchSendState.Cancelled : BatchSendState.Failed,
                    Error = approval.Reason ?? approval.Error,
                    ErrorKind = approval.State == ToolApprovalState.Denied ? "ApprovalDenied" : "ApprovalFailed"
                }
            };
        return new PreparedOutgoing { Messages = messages, ApprovalRequest = approvalRequest };
    }

    /// <summary>发送前重新检查相关政策和权限，但不重跑处理器或自动重新审批。</summary>
    internal async Task<BatchSendState?> CanCommitAsync(OutgoingBatch batch, PreparedOutgoing prepared, CancellationToken token)
    {
        BatchSendState? eligibility = await CanSendAsync(batch, token).ConfigureAwait(false);
        if (eligibility.HasValue) return eligibility;
        ConversationProfileSnapshot? current = _profiles.TryResolve(batch.Address);
        bool valid = prepared.ApprovalRequest is not null ? current is not null && _approvals.IsCurrent(prepared.ApprovalRequest, current)
            : current is null || !current.Profile.Approvals.Resolve(ApprovalActionIds.MessageSend, current.Profile.Execution).Enabled;
        return valid ? null : BatchSendState.Rejected;
    }

    /// <summary>普通插件发送不伪造任务身份，Agent 草稿共用任务权限判断。</summary>
    private async Task<BatchSendState?> CanSendAsync(OutgoingBatch batch, CancellationToken token)
    {
        if (batch.Origin.TaskId is null && batch.Origin.AgentId is null) return null;
        AgentActionAccess access = await _authorization.CheckAsync(batch.Address, batch.Origin, batch.SourceToolId, [], token).ConfigureAwait(false);
        return access switch { AgentActionAccess.Allowed => null, AgentActionAccess.Stopped => BatchSendState.Cancelled, _ => BatchSendState.Rejected };
    }

    /// <summary>期限覆盖实际处理函数，超时不提前释放仍未退出的处理器资格。</summary>
    private async Task<OutgoingProcessingResult> InvokeProcessorAsync(IOutgoingProcessorLease lease, OutgoingProcessingContext context, TimeSpan timeout, CancellationToken token)
    {
        using CancellationTokenSource deadline = new(timeout, _clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        Processing.Value++;
        try
        {
            OutgoingProcessingResult result = await lease.ProcessAsync(context, linked.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("出站处理超过期限。");
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("出站处理超过期限。"); }
        finally { Processing.Value--; }
    }

    /// <summary>数量、JSON 字节和段结构都在发送前验证，超限不静默截断。</summary>
    private static void Validate(ImmutableArray<MessageDraft> messages, OutgoingOptions options)
    {
        if (messages.IsDefaultOrEmpty || messages.Length > options.MaximumMessages) throw new ArgumentException("出站消息数量无效。");
        foreach (MessageDraft message in messages) ContentValidation.Validate(message.Segments);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(messages)) > options.MaximumJsonBytes) throw new ArgumentException("出站 JSON 内容超过配置上限。");
    }

    /// <summary>处理或存储失败只形成明确结果，不提供原文发送回退。</summary>
    private static PreparedOutgoing Failed(OutgoingBatch batch, int count, string kind, string error) => new()
    { Messages = [], Failure = new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Failed, TotalMessages = count, Recorded = true, ErrorKind = kind, Error = error } };

    /// <summary>权限拒绝与处理故障分别记录。</summary>
    private static PreparedOutgoing Rejected(OutgoingBatch batch, int count, BatchSendState state, string error) => new()
    { Messages = [], Failure = new BatchSendResult { BatchId = batch.Id, State = state, TotalMessages = count, Recorded = true, ErrorKind = state == BatchSendState.Cancelled ? "Cancelled" : "Permission", Error = error } };
}

/// <summary>准备阶段只交付共享内容和审批依据，不携带插件实例。</summary>
internal sealed record PreparedOutgoing
{
    public required ImmutableArray<MessageDraft> Messages { get; init; }
    public ToolApprovalRequest? ApprovalRequest { get; init; }
    public BatchSendResult? Failure { get; init; }
}
