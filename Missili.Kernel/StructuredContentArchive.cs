using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>宿主持有的结构化归档队列；恢复归档不触发聊天或事件处理。</summary>
public sealed class StructuredContentArchive : IStructuredContentStore, IAsyncDisposable
{
    private readonly IInfrastructureStore _store;
    private readonly IAdapterRuntime _adapters;
    private readonly MediaArchive _media;
    private readonly InfrastructureOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(1);
    private readonly Channel<StructuredContentSnapshot> _queue;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WaitSlot> _waiters = [];
    private Task? _pump;
    private Task[] _workers = [];
    private string? _failure;
    public bool IsHealthy => Volatile.Read(ref _failure) is null;

    /// <summary>准备有限容量，网络展开交给当前适配器而不是数据库事务。</summary>
    public StructuredContentArchive(IInfrastructureStore store, IAdapterRuntime adapters, MediaArchive media, InfrastructureOptions options,
        ILogger<StructuredContentArchive> logger, TimeProvider clock)
    {
        _store = store; _adapters = adapters; _media = media; _options = options; _clock = clock; _logger = logger;
        _queue = Channel.CreateBounded<StructuredContentSnapshot>(new BoundedChannelOptions(options.ContentCapacity)
        { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    /// <summary>数据库迁移完成后启动固定工作循环。</summary>
    public void Start()
    {
        if (_pump is not null) throw new InvalidOperationException("结构化归档已启动。");
        _workers = Enumerable.Range(0, _options.ContentConcurrency).Select(_ => Task.Run(ConsumeAsync)).ToArray();
        _pump = Task.Run(PumpAsync); Notify();
    }

    /// <summary>合并唤醒信号，实际任务仍由数据库保存。</summary>
    public void Notify() => _wake.Writer.TryWrite(0);

    /// <summary>读取状态和部分正文，不自动重试失败任务。</summary>
    public Task<StructuredContentSnapshot?> GetContentAsync(Guid id, CancellationToken cancellationToken = default) => _store.GetContentAsync(id, cancellationToken);

    /// <summary>显式请求引用补全或转发归档，稳定来源重复请求复用已有结果。</summary>
    public async Task<Guid> RequestContentAsync(ContentSource source, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Guid id = await _store.RequestContentAsync(source, _clock.GetUtcNow(), linked.Token).ConfigureAwait(false);
        Notify(); return id;
    }

    /// <summary>等待已有工作；取消当前等待不会取消共享归档。</summary>
    public async Task<StructuredContentSnapshot> WaitContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        WaitSlot slot;
        lock (_gate) { if (!_waiters.TryGetValue(id, out slot!)) _waiters[id] = slot = new WaitSlot(); slot.Count++; }
        try
        {
            StructuredContentSnapshot content = await _store.GetContentAsync(id, linked.Token).ConfigureAwait(false) ?? throw new KeyNotFoundException("结构化内容不存在。");
            if (content.State is ContentState.Pending or ContentState.Expanding or ContentState.WaitingForAdapter)
            {
                if (_failure is not null) throw new InvalidOperationException(_failure);
                await slot.Done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                content = (await _store.GetContentAsync(id, linked.Token).ConfigureAwait(false))!;
            }
            return content;
        }
        finally { lock (_gate) { if (--slot.Count == 0) _waiters.Remove(id); } }
    }

    /// <summary>按有限批次读取持久积压，队满等待而不为每条记录创建独立任务。</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                while (!_stop.IsCancellationRequested)
                {
                    ImmutableArray<StructuredContentSnapshot> batch = await _store.ClaimContentsAsync(_options.ContentCapacity, _stop.Token).ConfigureAwait(false);
                    if (batch.IsEmpty) break;
                    foreach (StructuredContentSnapshot content in batch) await _queue.Writer.WriteAsync(content, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception); }
        finally { _queue.Writer.TryComplete(); }
    }

    /// <summary>一个固定工作者逐项展开根任务，局部进度由解析器逐次提交。</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (StructuredContentSnapshot content in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                long revision = _adapters.Revision;
                StructuredContentSnapshot current = content;
                try
                {
                    await _adapters.ExpandContentAsync(content, SaveAsync, _stop.Token).ConfigureAwait(false);
                    if (current.State is ContentState.Pending or ContentState.Expanding or ContentState.WaitingForAdapter)
                        throw new InvalidDataException("解析器结束但未提交完整性状态。");
                    Complete(content.Id);
                }
                catch (AdapterUnavailableException)
                {
                    await _store.SetContentStateAsync(content.Id, ContentState.WaitingForAdapter, null, _stop.Token).ConfigureAwait(false);
                    if (_adapters.Revision != revision) { await _store.ResumeAdapterArchivesAsync(_stop.Token).ConfigureAwait(false); Notify(); _media.Notify(); }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    _logger.LogWarning("结构化内容 {ContentId} 展开失败：{ErrorType}", content.Id, exception.GetType().Name);
                    await _store.SetContentStateAsync(content.Id, current.Nodes.IsEmpty ? ContentState.Failed : ContentState.Partial,
                        new ContentIssue { Path = "$", Code = "fetch-failed", Description = "此部分内容获取失败：" + exception.GetType().Name }, _stop.Token).ConfigureAwait(false);
                    Complete(content.Id);
                }

                /// <summary>先持久化本次进度及媒体引用，再允许解析器继续远端请求。</summary>
                async Task SaveAsync(ContentExpansion progress, CancellationToken token)
                { current = await _store.SaveContentAsync(content.Id, progress, _clock.GetUtcNow(), token).ConfigureAwait(false); _media.Notify(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception); }
    }

    /// <summary>只唤醒已完成内容的等待者，不缓存无限历史任务。</summary>
    private void Complete(Guid id) { lock (_gate) if (_waiters.TryGetValue(id, out WaitSlot? slot)) slot.Done.TrySetResult(); }

    /// <summary>持久化基础设施失败时停止归档并结束等待，不能伪报完整内容。</summary>
    private void Fail(Exception exception)
    {
        Volatile.Write(ref _failure, "结构化内容归档存储失败。");
        _logger.LogError("结构化归档基础设施失败：{ErrorType}", exception.GetType().Name);
        _stop.Cancel();
    }

    /// <summary>等待实际解析调用退出，保留未完成进度供下次启动恢复。</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); _wake.Writer.TryComplete();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }

    /// <summary>工作结束后释放服务取消源。</summary>
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _stop.Dispose(); }

    /// <summary>共享等待计数归零后立即移除。</summary>
    private sealed class WaitSlot
    {
        public int Count { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
