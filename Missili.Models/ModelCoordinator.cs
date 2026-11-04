using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;
using Missili.Matrix;

namespace Missili.Models;

/// <summary>一次逻辑调用的候选策略、记录和取消所有者，提供商不再自行重试。</summary>
public sealed class ModelCoordinator : IModelService, IModelCallHistory, IAsyncDisposable
{
    private readonly IModelStore _store;
    private readonly IModelProviderRuntime _providers;
    private readonly FairInvocationScheduler _scheduler;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Guid _runId;
    private readonly string _details;
    private readonly object _gate = new();
    private readonly HashSet<Task> _calls = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _closing;

    /// <summary>复用公平调度实现，模型网络额度独立于消息处理器额度。</summary>
    public ModelCoordinator(IModelStore store, IModelProviderRuntime providers, ModelOptions options, string dataDirectory, Guid runId, ILogger logger, TimeProvider clock)
    {
        options.Validate(); _store = store; _providers = providers; _clock = clock; _logger = logger; _runId = runId;
        _scheduler = new FairInvocationScheduler(options.NetworkConcurrency, options.QueueCapacity);
        _details = Path.GetFullPath(Path.Combine(dataDirectory, "model-calls"));
    }

    /// <summary>直接请求使用固定消息集合；会话投影由模块入口在网络额度之外准备。</summary>
    public Task<ModelResult> GenerateAsync(ModelRequest request, IModelUpdateSink? updates = null, CancellationToken cancellationToken = default) =>
        GeneratePreparedAsync(request, null, updates, cancellationToken);

    /// <summary>图片组批在获取网络额度之前按实际驱动编码计量，不调用任何模型。</summary>
    internal async Task<long> MeasureAsync(ModelRequest request, string modelId, CancellationToken token)
    {
        ModelDefinition model = request.Profile.Models[modelId];
        ModelProviderDefinition provider = request.Profile.Providers[model.ProviderId];
        using IModelProviderLease lease = _providers.TryAcquire(provider.Protocol) ?? throw new ModelInputException("图片候选的提供商尚未加载。");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
        return await lease.MeasureAsync(new ModelAttemptRequest { AttemptId = Guid.NewGuid(), Request = request, Model = model, Provider = provider }, linked.Token).ConfigureAwait(false);
    }

    /// <summary>准备函数只改变候选的编码输入，不重新选择历史或修改配置快照。</summary>
    internal async Task<ModelResult> GeneratePreparedAsync(ModelRequest request,
        Func<ModelDefinition, CancellationToken, Task<ImmutableArray<ModelMessage>>>? prepare,
        IModelUpdateSink? updates, CancellationToken cancellationToken)
    {
        Validate(request);
        request = Freeze(request);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closing) return new ModelResult { CallId = request.Id, State = ModelCallState.Cancelled, Error = "模型模块正在关闭。" };
            _calls.Add(finished.Task);
        }
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { return await ExecuteAsync(request, prepare, updates, lifetime.Token).ConfigureAwait(false); }
        finally { lock (_gate) { _calls.Remove(finished.Task); finished.TrySetResult(); } }
    }

    /// <summary>记录失败结束本次逻辑调用，主动取消不进入任何备用候选。</summary>
    private async Task<ModelResult> ExecuteAsync(ModelRequest request,
        Func<ModelDefinition, CancellationToken, Task<ImmutableArray<ModelMessage>>>? prepare,
        IModelUpdateSink? updates, CancellationToken token)
    {
        ImmutableArray<ModelAttemptSummary>.Builder attempts = ImmutableArray.CreateBuilder<ModelAttemptSummary>();
        ModelResult last = new() { CallId = request.Id, State = ModelCallState.Failed, Error = "没有候选成功完成。" };
        try
        {
            await _store.CreateCallAsync(new ModelCallRecord
            {
                Id = request.Id,
                RunId = _runId,
                Purpose = request.Purpose,
                ProfileId = request.Profile.Profile.Id,
                ConfigurationRevision = request.Profile.Revision,
                Conversation = request.Conversation,
                TriggerMessageId = request.TriggerMessageId,
                StartedAt = _clock.GetUtcNow(),
                State = ModelCallState.Running
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return last with { State = ModelCallState.Cancelled, Error = "模型调用已取消。" }; }
        catch (Exception exception) { LogStorage(exception); return last with { State = ModelCallState.RecordingFailed, Error = "调用记录保存失败，未请求模型。" }; }

        try
        {
            ModelExecutionOptions policy = request.Profile.Profile.Execution;
            // 每轮按固定候选顺序推进；成功、主动取消或本地错误立即退出。
            for (int round = 1; round <= policy.Rounds; round++)
            {
                foreach (string id in request.Candidates)
                {
                    token.ThrowIfCancellationRequested();
                    ModelDefinition model = request.Profile.Models[id];
                    ModelProviderDefinition provider = request.Profile.Providers[model.ProviderId];
                    ModelRequest prepared = request;
                    if (prepare is not null) prepared = request with { Messages = await prepare(model, token).ConfigureAwait(false), Context = null };
                    (ModelResult result, ModelAttemptSummary summary, bool retry) = await AttemptAsync(prepared, model, provider, round, attempts.Count + 1, updates, token).ConfigureAwait(false);
                    attempts.Add(summary); last = result;
                    if (!retry || result.State == ModelCallState.Succeeded) return await FinishAsync(last with { Attempts = attempts.ToImmutable() }).ConfigureAwait(false);
                }
                if (round < policy.Rounds) await Task.Delay(policy.RoundDelay, _clock, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { last = last with { State = ModelCallState.Cancelled, Complete = false, Error = "模型调用已取消。" }; }
        catch (ModelInputException exception) { last = last with { State = ModelCallState.Failed, Complete = false, Error = exception.Message }; }
        catch (Exception exception)
        {
            LogStorage(exception);
            last = last with { State = ModelCallState.RecordingFailed, Complete = false, Error = "模型输入准备或基础设施失败，未继续请求。" };
        }
        return await FinishAsync(last with { Attempts = attempts.ToImmutable() }).ConfigureAwait(false);
    }

    /// <summary>一次尝试绑定代次；等待队列及释放诊断资源期间都持有调用资格。</summary>
    private async Task<(ModelResult, ModelAttemptSummary, bool)> AttemptAsync(ModelRequest request, ModelDefinition model, ModelProviderDefinition provider,
        int round, int sequence, IModelUpdateSink? updates, CancellationToken token)
    {
        using IModelProviderLease? lease = _providers.TryAcquire(provider.Protocol);
        ModelAttemptSummary summary = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            ProviderId = provider.Id,
            Generation = lease?.Generation,
            Round = round,
            Sequence = sequence,
            StartedAt = _clock.GetUtcNow(),
            State = ModelCallState.Running
        };
        ModelAttemptRequest attempt = new() { Request = request, AttemptId = summary.Id, Model = model, Provider = provider };
        ModelProtocolResult response = new() { State = ModelCallState.Failed, Error = "模型提供商尚未加载或正在重载。", ErrorKind = "Unavailable" };
        AttemptDiagnostics? detail = null;
        bool retry = true;
        try
        {
            await _store.SaveAttemptAsync(request.Id, summary, token).ConfigureAwait(false);
            detail = await AttemptDiagnostics.CreateAsync(_details, attempt, updates, token).ConfigureAwait(false);
            if (lease is not null)
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
                try
                {
                    Task<ModelProtocolResult> work = await _scheduler.EnqueueVersionedAsync(provider.Id, provider.MaxConcurrency, request.Profile.Revision,
                        callToken => lease.ExecuteAsync(attempt, detail, callToken), linked.Token).ConfigureAwait(false);
                    response = await work.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                { response = new ModelProtocolResult { State = ModelCallState.Cancelled, Content = detail.PartialContent, Error = "模型调用被取消。", ErrorKind = "Cancelled" }; }
                if (response.State == ModelCallState.Cancelled && !token.IsCancellationRequested)
                    response = response with { State = ModelCallState.Failed, Error = "提供商代次停止。", ErrorKind = "ProviderStopped" };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { response = new ModelProtocolResult { State = ModelCallState.Cancelled, Content = detail?.PartialContent ?? [], Error = "模型调用已取消。", ErrorKind = "Cancelled" }; retry = false; }
        catch (ModelInputException exception)
        { response = new ModelProtocolResult { State = ModelCallState.Failed, Error = exception.Message, ErrorKind = "Input" }; retry = false; }
        catch (ModelConsumerException)
        { response = new ModelProtocolResult { State = ModelCallState.Failed, Content = detail?.PartialContent ?? [], Error = "增量接收器执行失败。", ErrorKind = "Consumer" }; retry = false; }
        catch (Exception exception)
        {
            // 提供商应将协议失败转成共享值；未分类异常不触发重复计费。
            LogStorage(exception);
            response = new ModelProtocolResult { State = ModelCallState.RecordingFailed, Content = detail?.PartialContent ?? [], Error = "模型调用基础设施失败，未继续请求。", ErrorKind = "Infrastructure" };
            retry = false;
        }
        if (token.IsCancellationRequested) { response = response with { State = ModelCallState.Cancelled, Complete = false }; retry = false; }
        bool preserve = response.State != ModelCallState.Succeeded || request.Profile.Profile.Execution.CaptureSuccessfulDetails;
        try
        {
            if (detail is not null) await detail.SealAsync(response).ConfigureAwait(false);
            summary = summary with
            {
                State = response.State,
                FinishedAt = _clock.GetUtcNow(),
                Error = response.Error,
                ErrorKind = response.ErrorKind,
                HttpStatus = detail?.HttpStatus,
                RequestId = detail?.RequestId,
                Usage = response.Usage,
                HasPartialResult = !response.Complete && !response.Content.IsEmpty,
                DetailId = preserve ? detail?.DetailId : null
            };
            await _store.SaveAttemptAsync(request.Id, summary, CancellationToken.None).ConfigureAwait(false);
            if (!preserve) detail?.Discard();
        }
        catch (Exception exception)
        {
            LogStorage(exception); retry = false;
            response = response with { State = ModelCallState.RecordingFailed, Error = response.Complete ? "结果已取得，记录失败。" : "模型调用记录保存失败。" };
            summary = summary with { State = ModelCallState.RecordingFailed, Error = response.Error, DetailId = detail?.DetailId };
        }
        finally { if (detail is not null) await detail.DisposeAsync().ConfigureAwait(false); }
        ModelResult result = new()
        {
            CallId = request.Id,
            State = response.State,
            ModelId = model.Id,
            Content = response.Content,
            Complete = response.Complete,
            Usage = response.Usage,
            FinishReason = response.FinishReason,
            Error = response.Error,
            Continuation = response.Continuation is JsonElement continuation ? new ModelContinuation { Protocol = provider.Protocol, ProviderId = provider.Id, ModelId = model.Id, Items = continuation.Clone() } : null
        };
        return (result, summary, retry && response.State == ModelCallState.Failed);
    }

    /// <summary>结果已产生后的存储错误只改变返回语义，不调用备用模型。</summary>
    private async Task<ModelResult> FinishAsync(ModelResult result)
    {
        try { await _store.FinishCallAsync(result.CallId, result, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false); return result; }
        catch (Exception exception)
        { LogStorage(exception); return result with { State = ModelCallState.RecordingFailed, Error = result.Complete ? "结果已取得，记录失败。" : "模型调用记录保存失败。" }; }
    }

    /// <summary>检查候选归属和续接边界，不向其他协议发送原生状态。</summary>
    private static void Validate(ModelRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        if (request.Id == Guid.Empty || request.Candidates.IsDefaultOrEmpty || request.Messages.IsDefault || request.Tools.IsDefault)
            throw new ModelInputException("模型请求集合或标识无效。");
        if (request.Candidates.Distinct(StringComparer.Ordinal).Count() != request.Candidates.Length) throw new ModelInputException("模型候选不能重复。");
        if (request.Profile.Profile.Execution.Rounds < 1 || request.Profile.Profile.Execution.RoundDelay < TimeSpan.Zero) throw new ModelInputException("模型重试参数无效。");
        if (request.Tools.Any(tool => string.IsNullOrWhiteSpace(tool.Name) || tool.Parameters.ValueKind != JsonValueKind.Object) ||
            request.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Length)
            throw new ModelInputException("工具定义必须具有唯一名称和对象形式的参数模式。");
        foreach (string id in request.Candidates)
        {
            if (!request.Profile.Models.TryGetValue(id, out ModelDefinition? model) || !request.Profile.Providers.TryGetValue(model.ProviderId, out ModelProviderDefinition? provider))
                throw new ModelInputException("模型候选不存在。");
            if (request.Stream && !model.Streaming) throw new ModelInputException("候选不支持所请求的流式模式。");
            foreach (ModelMessage message in request.Messages)
            {
                if (message.Continuation is not ModelContinuation continuation) continue;
                if (message.Role != "assistant" || !message.Content.IsEmpty) throw new ModelInputException("原生续接应独立占据一个 assistant 轮次，不能重复附加通用输出。");
                if (continuation.Protocol != provider.Protocol || continuation.ProviderId != provider.Id || continuation.ModelId != model.Id)
                    throw new ModelInputException("原生续接只允许使用其所属协议、提供商和模型。");
            }
        }
    }

    /// <summary>复制所有跨调用 JSON 值，避免调用者释放文档影响在途请求。</summary>
    private static ModelRequest Freeze(ModelRequest request) => request with
    {
        Messages = request.Messages.Select(message => message with
        {
            Content = message.Content.Select(block => block with { Data = block.Data?.Clone() }).ToImmutableArray(),
            Continuation = message.Continuation is null ? null : message.Continuation with { Items = message.Continuation.Items.Clone() }
        }).ToImmutableArray(),
        Profile = request.Profile with { Models = request.Profile.Models.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Parameters = pair.Value.Parameters?.Clone() }, StringComparer.Ordinal) },
        Tools = request.Tools.Select(tool => tool with { Parameters = tool.Parameters.Clone() }).ToImmutableArray()
    };

    /// <summary>记录错误类别，不让日志持有插件异常对象或泄露请求正文。</summary>
    private void LogStorage(Exception exception) => _logger.LogError("模型调用基础设施错误：{ErrorType}", exception.GetType().Name);
    /// <summary>查询逻辑调用及各次尝试。</summary>
    public Task<ModelCallRecord?> GetCallAsync(Guid id, CancellationToken cancellationToken = default) => _store.GetCallAsync(id, cancellationToken);
    /// <summary>有限分页读取调用历史。</summary>
    public Task<ModelCallPage> QueryCallsAsync(ModelCallQuery query, CancellationToken cancellationToken = default) => _store.QueryCallsAsync(query, cancellationToken);
    /// <summary>按存储记录定位归档，不允许外部调用者指定任意路径。</summary>
    public async Task<ModelDetailRead?> OpenDetailAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        string? id = await _store.GetAttemptDetailAsync(attemptId, cancellationToken).ConfigureAwait(false);
        if (id is null) return null;
        if (!Guid.TryParseExact(id, "N", out Guid parsed) || parsed != attemptId) throw new InvalidDataException("调用详情标识损坏。");
        return new ModelDetailRead { Content = File.OpenRead(Path.Combine(_details, id + ".zip")), ContentType = "application/zip" };
    }

    /// <summary>停止新调用，取消并等待实际生成与记录结束后才关闭调度。</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] calls;
        lock (_gate) { if (_closing) return; _closing = true; calls = _calls.ToArray(); }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(calls).ConfigureAwait(false);
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
