using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>完整工具组的宿主结果，所有实际工作收尾后才允许模型续接。</summary>
public sealed record AgentToolGroupResult
{
    public ImmutableArray<AgentToolResult> Results { get; init; } = [];
    public ModelMessage? Message { get; init; }
    public bool CanContinue { get; init; }
    public string? Error { get; init; }
}

/// <summary>并行工具的唯一执行入口，负责准备、审批、一次执行、草稿接管及完整记录。</summary>
public sealed class AgentToolExecutor
{
    private readonly IAgentStore _agents;
    private readonly IAgentToolStore _store;
    private readonly IAgentToolRuntime _tools;
    private readonly IConversationProfileService _profiles;
    private readonly IToolApprovalService _approvals;
    private readonly AgentAuthorization _authorization;
    private readonly IMessageSender _sender;
    private readonly IInvocationScheduler _scheduler;
    private readonly AgentOptions _options;
    private readonly PluginExecutionOptions _execution;
    private readonly TimeProvider _clock;

    /// <summary>复用宿主服务与独立工具调度，不在执行器中管理插件或模型生命周期。</summary>
    public AgentToolExecutor(IAgentStore agents, IAgentToolStore store, IAgentToolRuntime tools, IConversationProfileService profiles,
        IToolApprovalService approvals, AgentAuthorization authorization, IMessageSender sender, IInvocationScheduler scheduler, AgentOptions options, PluginExecutionOptions execution, TimeProvider clock)
    { _agents = agents; _store = store; _tools = tools; _profiles = profiles; _approvals = approvals; _authorization = authorization; _sender = sender; _scheduler = scheduler; _options = options; _execution = execution; _clock = clock; }

    /// <summary>完整校验组结构后原子预留预算；各调用并行但最终结果按原请求顺序闭合。</summary>
    public async Task<AgentToolGroupResult> ExecuteAsync(AgentToolContext context, Guid turnId, Guid modelEntryId, ModelResult model,
        ImmutableArray<AgentToolDescriptor> capturedTools, AgentWorkLimiter working, CancellationToken queuedCancellation, CancellationToken executionCancellation)
    {
        ImmutableArray<ModelBlock> blocks = model.Content.Where(block => block.Kind == ModelBlockKind.ToolCall).ToImmutableArray();
        if (context.Origin.AgentId is not Guid agentId || model.State != ModelCallState.Succeeded || !model.Complete || blocks.IsEmpty || blocks.Length > _options.MaximumToolCallsPerResult ||
            blocks.Any(block => string.IsNullOrWhiteSpace(block.ToolCallId) || string.IsNullOrWhiteSpace(block.Name) || !block.Complete || !block.Data.HasValue) ||
            blocks.Select(block => block.ToolCallId).Distinct(StringComparer.Ordinal).Count() != blocks.Length)
            return new AgentToolGroupResult { Error = "模型工具组不完整、身份重复或超过数量上限。" };
        ImmutableArray<AgentToolIntent> intents = blocks.Select(block => new AgentToolIntent
        {
            ToolCallId = block.ToolCallId!,
            Name = block.Name!,
            Arguments = block.Data!.Value.Clone(),
            Tool = capturedTools.SingleOrDefault(tool => tool.Definition.Name == block.Name)
        }).ToImmutableArray();
        AgentToolReservation reservation;
        try { reservation = await _store.BeginToolGroupAsync(new AgentToolGroup { AgentId = agentId, TurnId = turnId, ModelEntryId = modelEntryId, Model = model, Calls = intents }, _clock.GetUtcNow(), queuedCancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (queuedCancellation.IsCancellationRequested) { return new AgentToolGroupResult { Error = "工具组在执行前取消。" }; }
        catch (Exception exception) { return new AgentToolGroupResult { Error = "调用意图未保存：" + exception.GetType().Name }; }
        if (!reservation.Created)
            return new AgentToolGroupResult
            {
                Results = reservation.Calls.Select(call => call.Result ?? new AgentToolResult { InvocationId = call.Intent.Id, State = call.State, Recorded = true }).ToImmutableArray(),
                Error = reservation.Error ?? "该工具组已接单，读取已有记录不会再次执行。"
            };
        AgentToolResult[] results = await Task.WhenAll(reservation.Calls.Select(call => ExecuteOneAsync(context with { Origin = call.Origin }, call, working, queuedCancellation, executionCancellation))).ConfigureAwait(false);
        bool recorded = results.All(result => result.Recorded);
        ModelMessage message = new()
        {
            Role = "tool",
            Content = results.Select((result, index) => new ModelBlock
            { Kind = ModelBlockKind.ToolResult, ToolCallId = blocks[index].ToolCallId, Name = blocks[index].Name, Data = Projection(result) }).ToImmutableArray()
        };
        if (recorded)
        {
            try { await _agents.AppendEntryAsync(agentId, AgentEntryKind.ToolResults, JsonSerializer.SerializeToElement(message), _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { return new AgentToolGroupResult { Results = results.ToImmutableArray(), Message = message, Error = "结果组记录失败：" + exception.GetType().Name }; }
        }
        return new AgentToolGroupResult { Results = results.ToImmutableArray(), Message = message, CanContinue = recorded, Error = recorded ? null : "已有工具结果记录失败，停止自动推进。" };
    }

    /// <summary>调用的每个失败点都有真实执行状态，任何分支都不重试工具。</summary>
    private async Task<AgentToolResult> ExecuteOneAsync(AgentToolContext context, AgentToolRecord call, AgentWorkLimiter working, CancellationToken queued, CancellationToken execution)
    {
        ScopedToolHistory history = new(context);
        context = context with { History = history, AgentHistory = history, ToolHistory = history, Search = history };
        ToolExecutionResult? pluginResult = null;
        bool started = false;
        AgentToolResult result;
        try
        {
            AgentToolDescriptor? descriptor = call.Intent.Tool;
            if (descriptor is null) return await FinishAsync(Failure(call, ToolCallState.Rejected, "Visibility", "工具不在本次模型可见目录中。")).ConfigureAwait(false);
            string? invalid = ValidateArguments(call.Intent.Arguments);
            if (invalid is not null) return await FinishAsync(Failure(call, ToolCallState.Rejected, "Arguments", invalid)).ConfigureAwait(false);
            queued.ThrowIfCancellationRequested();
            if (await _authorization.CheckAsync(context.Conversation, context.Origin, descriptor.Definition.Id, [], queued).ConfigureAwait(false) != AgentActionAccess.Allowed)
                return await FinishAsync(Failure(call, ToolCallState.Rejected, "Permission", "工具权限不可用或任务已停止。")).ConfigureAwait(false);
            PreparedToolCall prepared;
            using (IAgentToolLease? lease = _tools.TryAcquireTool(descriptor))
            {
                if (lease is null) return await FinishAsync(Failure(call, ToolCallState.Unavailable, "Generation", "捕获的工具代次不可用。")).ConfigureAwait(false);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(queued, lease.LifetimeToken);
                ScopedToolAssets assets = new(context, _authorization, descriptor.Definition.Id, false, false);
                Task<PreparedToolCall> work = await ScheduleAsync(descriptor,
                    token => lease.PrepareAsync(context with { Models = PreparationModels.Instance, Media = assets, Contents = assets, Transcriptions = assets, Assets = assets }, call.Intent.Arguments, token), linked.Token).ConfigureAwait(false);
                prepared = await work.ConfigureAwait(false);
            }
            await _store.PrepareToolAsync(call.Intent.Id, prepared, queued).ConfigureAwait(false);
            if (!prepared.Valid || ValidateArguments(prepared.Arguments!.Value) is not null)
                return await FinishAsync(Failure(call, ToolCallState.Rejected, "Arguments", prepared.Error ?? "准备参数无效或超限。")).ConfigureAwait(false);
            if (prepared.RequiredPermissions.IsDefault || prepared.RequiredPermissions.Distinct(StringComparer.Ordinal).Count() != prepared.RequiredPermissions.Length ||
                prepared.RequiredPermissions.Any(permission => !descriptor.Definition.DeclaredPermissions.Contains(permission, StringComparer.Ordinal)) ||
                await _authorization.CheckAsync(context.Conversation, context.Origin, descriptor.Definition.Id, prepared.RequiredPermissions, queued).ConfigureAwait(false) != AgentActionAccess.Allowed)
                return await FinishAsync(Failure(call, ToolCallState.Rejected, "Permission", "本次具体动作的附加权限未获授予。")).ConfigureAwait(false);
            ToolApprovalRequest approvalRequest = new()
            {
                ToolId = descriptor.Definition.Id,
                Description = descriptor.Definition.Description,
                Generation = descriptor.Generation,
                Prepared = prepared,
                PermissionCategory = descriptor.Definition.PermissionCategory,
                HasSideEffects = descriptor.Definition.HasSideEffects,
                Conversation = context.Conversation,
                Origin = context.Origin,
                Profile = context.Profile,
                Evidence = await _authorization.EvidenceAsync(context.Conversation, context.Origin, null, context.Profile, queued).ConfigureAwait(false)
            };
            ToolApprovalResult approval = await _approvals.ReviewAsync(approvalRequest, queued).ConfigureAwait(false);
            await _store.ApproveToolAsync(call.Intent.Id, approvalRequest, approval, CancellationToken.None).ConfigureAwait(false);
            if (!approval.Permitted) return await FinishAsync(Failure(call, approval.State == ToolApprovalState.Denied ? ToolCallState.ApprovalDenied : ToolCallState.ApprovalFailed,
                approval.ErrorKind ?? "Approval", approval.Reason ?? approval.Error ?? "审批没有放行。")).ConfigureAwait(false);
            using (IAgentToolLease? lease = _tools.TryAcquireTool(descriptor))
            {
                if (lease is null) return await FinishAsync(Failure(call, ToolCallState.Unavailable, "Generation", "批准对应的工具代次已失效。")).ConfigureAwait(false);
                using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(queued, lease.LifetimeToken);
                Task<ToolExecutionResult> work = await ScheduleAsync(descriptor, async schedulingToken =>
                {
                    // 排队取消只约束尚未开始的动作，优雅停止不取消已经运行的工具。
                    schedulingToken.ThrowIfCancellationRequested();
                    using IDisposable? permit = descriptor.Definition.IsControl ? null : await working.EnterAsync(context.Origin.AgentId!.Value, schedulingToken).ConfigureAwait(false);
                    ConversationProfileSnapshot? current = _profiles.TryResolve(context.Conversation);
                    if (current is null || !_approvals.IsCurrent(approvalRequest, current) ||
                        await _authorization.CheckAsync(context.Conversation, context.Origin, descriptor.Definition.Id, prepared.RequiredPermissions, admission.Token).ConfigureAwait(false) != AgentActionAccess.Allowed)
                        return new ToolExecutionResult { State = ToolCallState.Rejected, ErrorKind = "EligibilityChanged", Error = "批准后的相关政策或权限变化，需要重新提交。" };
                    if (!await _store.StartToolAsync(call.Intent.Id, _clock.GetUtcNow(), admission.Token).ConfigureAwait(false))
                        return new ToolExecutionResult { State = ToolCallState.CancelledBeforeExecution, Error = "工具开始前任务已停止。" };
                    started = true;
                    using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(execution, lease.LifetimeToken);
                    using CancellationTokenSource? timeout = descriptor.Definition.ExecutionTimeout is TimeSpan limit ? new CancellationTokenSource(limit, _clock) : null;
                    using CancellationTokenSource actual = timeout is null ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, timeout.Token);
                    ScopedToolAssets assets = new(context, _authorization, descriptor.Definition.Id, true, prepared.RequiredPermissions.Contains(AgentPermissions.RefreshTranscription, StringComparer.Ordinal));
                    AgentToolContext scoped = context with
                    {
                        Models = new ScopedModels(context.Models, _agents, context, actual.Token, queued),
                        MaximumResultBytes = _options.MaximumToolResultBytes,
                        Media = assets,
                        Contents = assets,
                        Transcriptions = assets,
                        Assets = assets
                    };
                    ToolExecutionResult value = await lease.ExecuteAsync(scoped, prepared, actual.Token).ConfigureAwait(false);
                    return timeout?.IsCancellationRequested == true ? value with { State = ToolCallState.CancelledDuringExecution, ErrorKind = "Timeout", Error = "工具执行超过期限。" } : value;
                }, admission.Token).ConfigureAwait(false);
                pluginResult = await work.ConfigureAwait(false);
                if (pluginResult.State is ToolCallState.Pending or ToolCallState.Preparing or ToolCallState.Reviewing or ToolCallState.Running or ToolCallState.Completing || !Enum.IsDefined(pluginResult.State))
                    pluginResult = pluginResult with { State = ToolCallState.Unknown, Complete = false, Error = "插件返回了非法终局状态。", ErrorKind = "Contract" };
                if (started) await _store.SaveToolExecutionAsync(call.Intent.Id, pluginResult, CancellationToken.None).ConfigureAwait(false);
            }
            result = new AgentToolResult { InvocationId = call.Intent.Id, State = pluginResult.State, Execution = pluginResult, Recorded = true, Error = pluginResult.Error, ErrorKind = pluginResult.ErrorKind };
            if (Encoding.UTF8.GetByteCount(Projection(result).GetRawText()) > _options.MaximumToolResultBytes)
                result = result with { State = ToolCallState.Failed, ErrorKind = "OutputLimit", Error = "工具结果超过模型内联上限；完整输出保存在调用记录中。" };
            else if (!pluginResult.Drafts.IsEmpty && pluginResult.State == ToolCallState.Succeeded && pluginResult.Complete)
            {
                execution.ThrowIfCancellationRequested();
                BatchSendResult sending = await _sender.SendAsync(new OutgoingBatch
                { Address = context.Conversation, Messages = pluginResult.Drafts, Origin = context.Origin, SourceToolId = descriptor.Definition.Id }, queued).ConfigureAwait(false);
                result = result with
                {
                    Sending = sending,
                    Recorded = sending.Recorded,
                    State = sending.State is BatchSendState.Completed or BatchSendState.Filtered ? ToolCallState.Succeeded : sending.State == BatchSendState.Unconfirmed ? ToolCallState.Unknown : ToolCallState.Failed,
                    Error = sending.Error,
                    ErrorKind = sending.ErrorKind
                };
            }
        }
        catch (OperationCanceledException)
        { result = Failure(call, started ? ToolCallState.CancelledDuringExecution : ToolCallState.CancelledBeforeExecution, "Cancelled", "工具调用已取消。") with { Execution = pluginResult }; }
        catch (Exception exception)
        { result = Failure(call, ToolCallState.RecordingFailed, "Infrastructure", "调用协调或记录失败：" + exception.GetType().Name) with { Execution = pluginResult, Recorded = false }; }
        // 发送回执及故障说明也会增加投影大小，最终结果必须再次满足同一内联限额。
        if (Encoding.UTF8.GetByteCount(Projection(result).GetRawText()) > _options.MaximumToolResultBytes)
            result = result with { State = result.Recorded ? ToolCallState.Failed : ToolCallState.RecordingFailed, ErrorKind = "OutputLimit", Error = "最终工具结果超过模型内联上限；完整结果及发送状态保存在调用记录中。" };
        return await FinishAsync(result).ConfigureAwait(false);
    }

    /// <summary>记录失败保留已经取得的插件结果和发送回执，调用者必须停止自动推进。</summary>
    private async Task<AgentToolResult> FinishAsync(AgentToolResult result)
    {
        try { await _store.FinishToolAsync(result, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false); return result; }
        catch (Exception exception) { return result with { State = ToolCallState.RecordingFailed, Recorded = false, ErrorKind = "Storage", Error = "工具最终结果保存失败：" + exception.GetType().Name }; }
    }

    /// <summary>框架控制操作不占普通插件工具槽，等待不会阻塞子代理执行。</summary>
    private Task<Task<T>> ScheduleAsync<T>(AgentToolDescriptor descriptor, Func<CancellationToken, Task<T>> action, CancellationToken token)
        => descriptor.Definition.IsControl ? Task.FromResult(action(token))
            : _scheduler.EnqueueAsync(descriptor.PluginId, Math.Min(_execution.ToolsAndProcessors.PerPluginConcurrency, descriptor.MaxConcurrency), action, token);

    /// <summary>参数结构先由宿主检查，工具准备阶段再校验业务字段。</summary>
    private string? ValidateArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return "工具参数必须是 JSON 对象。";
        string json = arguments.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > _options.MaximumToolArgumentBytes) return "工具参数超过字节上限。";
        try { using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = _options.MaximumJsonDepth }); return HasDuplicate(document.RootElement) ? "工具参数包含重复字段。" : null; }
        catch (JsonException) { return "工具参数超过 JSON 深度限制。"; }
    }

    /// <summary>每一层独立检查重名字段，嵌套对象不能隐藏另一个实际参数。</summary>
    private static bool HasDuplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) if (!names.Add(property.Name) || HasDuplicate(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) if (HasDuplicate(item)) return true;
        return false;
    }

    /// <summary>模型只看有界业务内容和可读取身份，不混入原始审批、签名或完整草稿。</summary>
    private JsonElement Projection(AgentToolResult result)
    {
        bool oversize = result.ErrorKind == "OutputLimit";
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["invocation_id"] = result.InvocationId,
            ["state"] = result.State.ToString(),
            ["recorded"] = result.Recorded,
            ["complete"] = result.Execution?.Complete,
            ["effect"] = result.Execution?.Effect.ToString(),
            ["data"] = oversize ? null : result.Execution?.ModelData ?? result.Execution?.Data,
            ["text"] = oversize || result.Execution?.ModelData.HasValue == true ? null : result.Execution?.Text,
            ["sending"] = oversize && result.Sending is BatchSendResult sent
                ? new Dictionary<string, object> { ["batch_id"] = sent.BatchId, ["state"] = sent.State.ToString(), ["recorded"] = sent.Recorded, ["unattempted_from"] = sent.UnattemptedFrom }
                : result.Sending,
            ["error"] = result.Error,
            ["error_kind"] = result.ErrorKind
        });
    }

    /// <summary>预执行失败明确保留调用身份，并由统一终局写入路径记录。</summary>
    private static AgentToolResult Failure(AgentToolRecord call, ToolCallState state, string kind, string error) => new()
    { InvocationId = call.Intent.Id, State = state, Recorded = true, ErrorKind = kind, Error = error };

    /// <summary>工具模型调用固定会话及归属，不能通过自报字段逃避任务预算。</summary>
    private sealed class ScopedModels : IModelService
    {
        private readonly IModelService _inner;
        private readonly IAgentStore _store;
        private readonly AgentToolContext _context;
        private readonly CancellationToken _lifetime;
        private readonly CancellationToken _admission;
        /// <summary>一次执行范围绑定一次不可变工具上下文。</summary>
        public ScopedModels(IModelService inner, IAgentStore store, AgentToolContext context, CancellationToken lifetime, CancellationToken admission)
        { _inner = inner; _store = store; _context = context; _lifetime = lifetime; _admission = admission; }
        /// <summary>预留逻辑调用预算后调用已有模型服务，内部候选策略仍由模型层管理。</summary>
        public async Task<ModelResult> GenerateAsync(ModelRequest request, IModelUpdateSink? updates = null, CancellationToken cancellationToken = default,
            CancellationToken admissionCancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
            using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, _admission, admissionCancellationToken);
            if (!await _store.ReserveCallsAsync(_context.Origin.AgentId!.Value, 1, 0, false, false, admission.Token).ConfigureAwait(false))
                return new ModelResult { CallId = request.Id, State = ModelCallState.Failed, Error = "任务已停止或模型预算耗尽。" };
            return await _inner.GenerateAsync(request with
            {
                Origin = _context.Origin,
                Conversation = _context.Conversation,
                Profile = _context.Profile with { Profile = _context.Profile.Profile with { Execution = request.Profile.Profile.Execution } }
            }, updates, linked.Token, admission.Token).ConfigureAwait(false);
        }
    }

    /// <summary>准备阶段不能通过共享模型入口产生网络业务动作。</summary>
    private sealed class PreparationModels : IModelService
    {
        public static PreparationModels Instance { get; } = new();
        /// <summary>准备只能解释输入与目标，模型调用必须在批准后的执行阶段进行。</summary>
        public Task<ModelResult> GenerateAsync(ModelRequest request, IModelUpdateSink? updates = null, CancellationToken cancellationToken = default,
            CancellationToken admissionCancellationToken = default)
            => throw new InvalidOperationException("工具准备阶段不能调用模型。");
    }
}
