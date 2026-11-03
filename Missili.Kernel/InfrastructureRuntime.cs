using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>接收、持久化和业务分发的宿主生命周期，所有外部业务仍通过契约接入。</summary>
public sealed class InfrastructureRuntime : IMessageIngress, IPlatformEventIngress, IAsyncDisposable
{
    private readonly IInfrastructureStore _store;
    private readonly MediaArchive _media;
    private readonly StructuredContentArchive _contents;
    private readonly MessageDispatcher _dispatcher;
    private readonly PlatformEventDispatcher _events;
    private readonly IPluginRuntime _plugins;
    private readonly IInvocationScheduler _scheduler;
    private readonly MessageSender _sender;
    private readonly InfrastructureOptions _options;
    private readonly DispatchSignal _signal;
    private readonly Guid _runId;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Channel<ReceiveWork> _ingress;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<Task> _flows = [];
    private Task? _receivePump;
    private Task? _dispatchPump;
    private Task? _archiveChanges;
    private bool _accepting;
    private bool _stopped;
    private bool _disposed;
    private string? _failure;

    public IMessageHistory History => _store;
    public IPlatformEventHistory Events => _store;
    public IMediaStore Media => _media;
    public IMediaImporter MediaImporter => _media;
    public IStructuredContentStore Contents => _contents;
    public IMediaTranscriptionStore Transcriptions => _store;
    public IPluginManager Plugins => _plugins;
    public IHookOrderService Orders { get; }
    public IMessageSender Sender { get; }
    public IAdapterRuntime Adapters { get; }
    public bool IsReady => Volatile.Read(ref _accepting) && _media.IsHealthy && _contents.IsHealthy && Volatile.Read(ref _failure) is null;

    /// <summary>组装实际服务但不提前启动接收；调用者负责提供同一个运行标识。</summary>
    public InfrastructureRuntime(IInfrastructureStore store, MediaArchive media, MessageDispatcher dispatcher, IPluginRuntime plugins,
        IInvocationScheduler scheduler, HookOrderService orders, MessageSender sender, DispatchSignal signal, Guid runId,
        InfrastructureOptions options, ILogger<InfrastructureRuntime> logger, TimeProvider clock, IAdapterRuntime adapters, StructuredContentArchive contents)
    {
        options.Validate();
        _store = store; _media = media; _dispatcher = dispatcher; _plugins = plugins; _scheduler = scheduler;
        _events = new PlatformEventDispatcher(plugins, orders, scheduler, logger);
        Orders = orders; Sender = sender; _sender = sender; _signal = signal; _runId = runId; _options = options; _logger = logger;
        _clock = clock;
        Adapters = adapters;
        _contents = contents;
        _ingress = Channel.CreateBounded<ReceiveWork>(new BoundedChannelOptions(options.IngressCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
    }

    /// <summary>事件和消息共享有限接收容量，保存完成后才返回成功回执。</summary>
    public async Task<EventReceipt> ReceiveEventAsync(IncomingPlatformEvent platformEvent, CancellationToken cancellationToken = default)
    {
        platformEvent.Validate();
        if (!Volatile.Read(ref _accepting)) throw new InvalidOperationException("运行时尚未就绪或已停止接收。");
        ReceiveWork work = new() { Event = platformEvent.Freeze(), ReceivedAt = _clock.GetUtcNow(), Caller = cancellationToken };
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _ingress.Writer.WriteAsync(work, admission.Token).ConfigureAwait(false);
        return await work.EventCompletion.Task.WaitAsync(admission.Token).ConfigureAwait(false);
    }

    /// <summary>先完成迁移和状态恢复，再启动工作循环并允许接收。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_receivePump is not null || _stopped) throw new InvalidOperationException("运行时不能重复启动。");
            await _store.InitializeAsync(_runId, cancellationToken).ConfigureAwait(false);
            await _media.StartAsync(cancellationToken).ConfigureAwait(false);
            _contents.Start();
            _archiveChanges = Task.Run(WatchAdaptersAsync);
            _receivePump = Task.Run(ReceivePumpAsync);
            _dispatchPump = Task.Run(DispatchPumpAsync);
            Volatile.Write(ref _accepting, true);
            _signal.Pulse();
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>正常状态与实际存储查询都成功时才报告就绪。</summary>
    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady) return false;
        try { await _store.CheckHealthAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { _logger.LogError(exception, "存储健康检查失败"); return false; }
    }

    /// <summary>进入有限接收队列；调用者取消等待不撤销已经提交的历史。</summary>
    public async Task<ReceiveReceipt> ReceiveAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        message.Validate();
        if (!Volatile.Read(ref _accepting)) throw new InvalidOperationException("运行时尚未就绪或已停止接收。");
        ReceiveWork work = new() { Message = message.Freeze(), ReceivedAt = _clock.GetUtcNow(), Caller = cancellationToken };
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _ingress.Writer.WriteAsync(work, admission.Token).ConfigureAwait(false);
        return await work.Completion.Task.WaitAsync(admission.Token).ConfigureAwait(false);
    }

    /// <summary>按照接收队列顺序保存，提交后只唤醒独立分发和媒体任务。</summary>
    private async Task ReceivePumpAsync()
    {
        try
        {
            await foreach (ReceiveWork work in _ingress.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    // 入队完成后快照属于宿主；网络断开或插件重载不能撤销已接受数据。
                    if (work.Message is not null)
                    {
                        ReceiveReceipt receipt = await _store.AppendAsync(work.Message, work.ReceivedAt, _runId, CancellationToken.None).ConfigureAwait(false);
                        if (!receipt.Duplicate && receipt.Message is not null) { _media.Notify(); _contents.Notify(); _signal.Pulse(); }
                        work.Completion.TrySetResult(receipt);
                    }
                    else
                    {
                        EventReceipt receipt = await _store.AppendEventAsync(work.Event!, work.ReceivedAt, _runId, CancellationToken.None).ConfigureAwait(false);
                        if (!receipt.Duplicate && receipt.Event is not null) _signal.Pulse();
                        work.EventCompletion.TrySetResult(receipt);
                    }
                }
                catch (OperationCanceledException) when (work.Caller.IsCancellationRequested) { work.Cancel(work.Caller); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "消息保存失败，未分发该条消息");
                    work.Completion.TrySetResult(new ReceiveReceipt { Error = "消息保存失败：" + exception.GetType().Name });
                    work.EventCompletion.TrySetResult(new EventReceipt { Error = "事件保存失败：" + exception.GetType().Name });
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            while (_ingress.Reader.TryRead(out ReceiveWork? work)) work.Cancel(_stop.Token);
        }
    }

    /// <summary>仅在适配器目录或网络能力变化时恢复归档，不轮询离线实例。</summary>
    private async Task WatchAdaptersAsync()
    {
        try
        {
            long revision = -1;
            while (!_stop.IsCancellationRequested)
            {
                await Adapters.WaitForChangeAsync(revision, _stop.Token).ConfigureAwait(false);
                revision = Adapters.Revision;
                await _store.ResumeAdapterArchivesAsync(_stop.Token).ConfigureAwait(false);
                _media.Notify(); _contents.Notify();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Volatile.Write(ref _failure, "适配器归档恢复失败。"); _logger.LogError(exception, "适配器归档恢复失败"); }
    }

    /// <summary>只领取活动额度内的业务记录，完成通知负责再次唤醒。</summary>
    private async Task DispatchPumpAsync()
    {
        bool eventsFirst = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                _flows.RemoveWhere(task => task.IsCompletedSuccessfully);
                int capacity = _options.MessageConcurrency - _flows.Count;
                if (capacity == 0) continue;
                // 两类业务轮流优先领取，单槽配置下持续消息不能饿死通知。
                eventsFirst = !eventsFirst;
                ImmutableArray<PlatformEventSnapshot> events = eventsFirst
                    ? await _store.ClaimEventsAsync(_runId, Math.Max(1, capacity / 2), _stop.Token).ConfigureAwait(false) : [];
                ImmutableArray<MessageSnapshot> messages = capacity > events.Length
                    ? await _store.ClaimMessagesAsync(_runId, capacity - events.Length, _stop.Token).ConfigureAwait(false) : [];
                if (!eventsFirst && messages.Length < capacity)
                    events = await _store.ClaimEventsAsync(_runId, capacity - messages.Length, _stop.Token).ConfigureAwait(false);
                foreach (MessageSnapshot message in messages)
                {
                    Task flow = RunFlowAsync(message);
                    _flows.Add(flow);
                    // 必须在 Task 已完成之后唤醒，避免先消费通知却仍看见所有活动槽占满。
                    _ = flow.ContinueWith(static (_, state) => ((DispatchSignal)state!).Pulse(), _signal,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                foreach (PlatformEventSnapshot platformEvent in events)
                {
                    Task flow = RunEventFlowAsync(platformEvent);
                    _flows.Add(flow);
                    _ = flow.ContinueWith(static (_, state) => ((DispatchSignal)state!).Pulse(), _signal,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if ((!messages.IsEmpty || !events.IsEmpty) && _flows.Count < _options.MessageConcurrency) _signal.Pulse();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, "消息调度存储失败。");
            _logger.LogError(exception, "消息分发基础设施失败");
        }
        finally { await Task.WhenAll(_flows).ConfigureAwait(false); _flows.Clear(); }
    }

    /// <summary>事件业务结束状态单独持久化，重启不再次分发。</summary>
    private async Task RunEventFlowAsync(PlatformEventSnapshot platformEvent)
    {
        ProcessingState state = ProcessingState.Finished;
        try { await _events.DispatchAsync(platformEvent, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { state = ProcessingState.Interrupted; }
        catch (Exception exception)
        { state = ProcessingState.Interrupted; _logger.LogError(exception, "事件 {EventId} 分发基础设施失败", platformEvent.Id); }
        try { await _store.SetEventProcessingAsync(platformEvent.Id, state, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        { Volatile.Write(ref _failure, "事件最终状态保存失败。"); _logger.LogError(exception, "事件 {EventId} 的最终状态保存失败", platformEvent.Id); }
    }

    /// <summary>业务完成后保存实际状态；插件异常已经在调用边界处理。</summary>
    private async Task RunFlowAsync(MessageSnapshot message)
    {
        ProcessingState state = ProcessingState.Finished;
        try { await _dispatcher.DispatchAsync(message, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { state = ProcessingState.Interrupted; }
        catch (Exception exception)
        {
            state = ProcessingState.Interrupted;
            _logger.LogError(exception, "消息 {MessageId} 分发基础设施失败", message.Id);
        }
        try { await _store.SetProcessingAsync(message.Id, state, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, "消息最终状态保存失败。");
            _logger.LogError(exception, "消息 {MessageId} 的最终状态保存失败", message.Id);
        }
    }

    /// <summary>先请求插件和下载停止，再等待分发；不能反过来等待需要停止回调才能退出的插件。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            Volatile.Write(ref _accepting, false);
            _ingress.Writer.TryComplete();
            await _stop.CancelAsync().ConfigureAwait(false);
            Task<PluginStatus[]> plugins = Task.WhenAll(_plugins.GetStatuses().Select(status => _plugins.StopAsync(status.Id, cancellationToken)));
            Task sends = _sender.StopAsync(cancellationToken);
            Task media = _media.StopAsync();
            Task contents = _contents.StopAsync();
            if (_receivePump is not null) await _receivePump.WaitAsync(cancellationToken).ConfigureAwait(false);
            PluginStatus[] states = await plugins.ConfigureAwait(false);
            if (states.Any(status => status.HasLiveGeneration || status.ActiveCalls > 0 || status.OwnedResources > 0))
                throw new InvalidOperationException("仍有插件未实际退出，运行时保留资源跟踪，未关闭存储。");
            if (_dispatchPump is not null) await _dispatchPump.WaitAsync(cancellationToken).ConfigureAwait(false);
            await sends.ConfigureAwait(false);
            await media.WaitAsync(cancellationToken).ConfigureAwait(false);
            await contents.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_archiveChanges is not null) await _archiveChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _scheduler.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            await _store.InterruptRunAsync(_runId, CancellationToken.None).ConfigureAwait(false);
            _stopped = true;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>所有登记工作退出后才释放归档及存储，重复释放不重复执行。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        await _media.DisposeAsync().ConfigureAwait(false);
        await _contents.DisposeAsync().ConfigureAwait(false);
        await _sender.DisposeAsync().ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _disposed = true;
    }

    /// <summary>队列中的有限接收请求，只保存原始值和接收回执。</summary>
    private sealed class ReceiveWork
    {
        public IncomingMessage? Message { get; init; }
        public IncomingPlatformEvent? Event { get; init; }
        public required DateTimeOffset ReceivedAt { get; init; }
        public required CancellationToken Caller { get; init; }
        public TaskCompletionSource<ReceiveReceipt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<EventReceipt> EventCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>取消尚未提交的队列项，不留下永不结束的等待。</summary>
        public void Cancel(CancellationToken token) { Completion.TrySetCanceled(token); EventCompletion.TrySetCanceled(token); }
    }

}
