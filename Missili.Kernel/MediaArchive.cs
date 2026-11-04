using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>宿主拥有的有界归档服务；插件只等待结果，不取得下载任务的取消权。</summary>
public sealed class MediaArchive : IMediaStore, IMediaImporter, IAsyncDisposable
{
    private readonly IInfrastructureStore _store;
    private readonly InfrastructureOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IAdapterRuntime? _adapters;
    private readonly SemaphoreSlim _slots;
    private readonly HashSet<Task> _imports = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(1);
    private readonly Channel<MediaWork> _queue;
    private readonly ConcurrentDictionary<string, Lazy<Task<BlobResult>>> _downloads = new();
    private readonly object _waitGate = new();
    private readonly Dictionary<Guid, WaitSlot> _waiters = [];
    private Task[] _workers = [];
    private Task? _pump;
    private string? _failure;

    /// <summary>创建专用 HTTP 客户端和有限队列，连接使用正常 TLS 与系统代理。</summary>
    public MediaArchive(IInfrastructureStore store, InfrastructureOptions options, ILogger<MediaArchive> logger, TimeProvider? clock = null, IAdapterRuntime? adapters = null)
    {
        options.Validate();
        _store = store;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _adapters = adapters;
        _slots = new SemaphoreSlim(options.DownloadConcurrency, options.DownloadConcurrency);
        _root = Path.GetFullPath(options.DataDirectory);
        SocketsHttpHandler handler = new()
        {
            ConnectTimeout = options.ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _queue = Channel.CreateBounded<MediaWork>(new BoundedChannelOptions(options.DownloadCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, AllowSynchronousContinuations = false });
    }

    public bool IsHealthy => Volatile.Read(ref _failure) is null;
    public event Action? ArchiveCompleted;

    /// <summary>在数据库恢复完成后启动固定工作数，并处理已有待归档记录。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pump is not null) throw new InvalidOperationException("媒体归档已启动。");
        Directory.CreateDirectory(PathFor("media/staging"));
        Directory.CreateDirectory(PathFor("media/objects"));
        _workers = Enumerable.Range(0, _options.DownloadConcurrency).Select(_ => Task.Run(ConsumeAsync)).ToArray();
        _pump = Task.Run(PumpAsync);
        Notify();
        return Task.CompletedTask;
    }

    /// <summary>只发送唤醒信号；真实积压保存在数据库中，信号合并不会丢失任务。</summary>
    public void Notify() => _wake.Writer.TryWrite(0);

    /// <summary>读取公开状态，不启动下载或重试。</summary>
    public async Task<MediaStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _store.GetMediaAsync(id, cancellationToken).ConfigureAwait(false))?.Status;

    /// <summary>登记等待后再次检查状态，避免恰好错过归档完成通知。</summary>
    public async Task<MediaReadResult> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_pump is null) throw new InvalidOperationException("媒体服务尚未启动。");
        using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        WaitSlot slot;
        lock (_waitGate)
        {
            if (!_waiters.TryGetValue(id, out slot!)) _waiters[id] = slot = new WaitSlot();
            slot.Count++;
        }
        try
        {
            MediaWork work = await _store.GetMediaAsync(id, waitCancellation.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("媒体引用不存在。");
            // 下载仍由宿主继续；此处仅等待当前调用所需的归档结果。
            if (work.Status.State is MediaState.Pending or MediaState.Downloading or MediaState.Finalizing or MediaState.WaitingForAdapter)
            {
                string? serviceError = Volatile.Read(ref _failure);
                if (serviceError is not null) return new MediaReadResult { Status = work.Status, Error = serviceError };
                string? error = await slot.Completion.Task.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
                if (error is not null) return new MediaReadResult { Status = work.Status, Error = error };
                work = await _store.GetMediaAsync(id, waitCancellation.Token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("媒体引用不存在。");
            }
            if (work.Status.State != MediaState.Available)
                return new MediaReadResult { Status = work.Status, Error = work.Status.State == MediaState.Cleaned ? "原件已清理。" : work.Status.Error ?? "原件不可用。" };
            waitCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                FileStream content = new(PathFor(work.RelativePath!), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return new MediaReadResult { Status = work.Status, Content = content };
            }
            catch (IOException) { return new MediaReadResult { Status = work.Status, Error = "原件缺失或暂不可读取。" }; }
            catch (UnauthorizedAccessException) { return new MediaReadResult { Status = work.Status, Error = "原件读取权限不足。" }; }
        }
        finally
        {
            lock (_waitGate)
            {
                slot.Count--;
                if (slot.Count == 0) _waiters.Remove(id);
            }
        }
    }

    /// <summary>按有限批次从持久化状态领取任务，队列满时停止继续读取。</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                // 一直填充到没有待处理记录；容量限制始终由队列提供。
                while (!_stop.IsCancellationRequested)
                {
                    ImmutableArray<MediaWork> batch = await _store.ClaimMediaAsync(_options.DownloadCapacity, _stop.Token).ConfigureAwait(false);
                    if (batch.IsEmpty) break;
                    foreach (MediaWork work in batch) await _queue.Writer.WriteAsync(work, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { FaultArchive(exception); }
        finally { _queue.Writer.TryComplete(); }
    }

    /// <summary>固定数量工作循环等待各自下载，不为数据库积压逐条创建 Task。</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (MediaWork work in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                long revision = _adapters?.Revision ?? 0;
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    await ArchiveAsync(work).ConfigureAwait(false);
                    CompleteWaiters(work.Status.Id, null);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (AdapterUnavailableException)
                {
                    await _store.WaitMediaForAdapterAsync(work.Status.Id, _stop.Token).ConfigureAwait(false);
                    // 状态变化可能早于本次等待提交，此时主动重新领取，避免错过唯一唤醒。
                    if (_adapters?.Revision != revision) { await _store.ResumeAdapterArchivesAsync(_stop.Token).ConfigureAwait(false); Notify(); }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("媒体 {MediaId} 归档失败：{ErrorType}", work.Status.Id, exception.GetType().Name);
                    try
                    {
                        await _store.FailMediaAsync(work.Status.Id, exception is MediaAcquisitionException ? exception.Message : "原件获取失败：" + exception.GetType().Name, _stop.Token).ConfigureAwait(false);
                        CompleteWaiters(work.Status.Id, null);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception storageError) { FaultArchive(storageError); break; }
                }
                finally { _slots.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { FaultArchive(exception); }
    }

    /// <summary>恢复已登记文件操作，或复用稳定来源对应的归档工作。</summary>
    private async Task ArchiveAsync(MediaWork work)
    {
        if (work.Status.State == MediaState.Finalizing)
        {
            PlaceFile(work.TemporaryPath!, work.RelativePath!, work.Status.Length!.Value);
            await _store.CompleteMediaAsync(work.Status.Id, work.Status.ContentKey!, work.Status.Length.Value, work.RelativePath!, _stop.Token).ConfigureAwait(false);
            return;
        }
        if (work.SourceKey is not null)
        {
            MediaWork? reusable = await _store.FindReusableMediaAsync(work.SourceKey, _stop.Token).ConfigureAwait(false);
            if (reusable?.RelativePath is not null && File.Exists(PathFor(reusable.RelativePath)))
            {
                await _store.CompleteMediaAsync(work.Status.Id, reusable.Status.ContentKey!, reusable.Status.Length!.Value, reusable.RelativePath, _stop.Token).ConfigureAwait(false);
                await _store.SetMediaRepresentationAsync(work.Status.Id, reusable.Status.Representation, _stop.Token).ConfigureAwait(false);
                return;
            }
        }
        string key = work.SourceKey is null ? "reference:" + work.Status.Id : "source:" + work.SourceKey;
        Lazy<Task<BlobResult>> pending = _downloads.GetOrAdd(key, _ => new Lazy<Task<BlobResult>>(() => DownloadAsync(work)));
        try
        {
            BlobResult result = await pending.Value.ConfigureAwait(false);
            await _store.CompleteMediaAsync(work.Status.Id, result.Key, result.Length, result.Path, _stop.Token).ConfigureAwait(false);
            await _store.SetMediaRepresentationAsync(work.Status.Id, result.Representation, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<BlobResult>>>(key, pending));
        }
    }

    /// <summary>直接下载由宿主持有；平台解析只在必须借助适配器时取得其代次资格。</summary>
    private async Task<BlobResult> DownloadAsync(MediaWork work)
    {
        if (work.Source.Kind == "http") return await DownloadHttpAsync(work, work.Source, _stop.Token).ConfigureAwait(false);
        if (_adapters is null || work.Source.AdapterId is null) throw new AdapterUnavailableException();
        await using MediaSourceRead resolved = await _adapters.ResolveMediaAsync(work.Source, _stop.Token).ConfigureAwait(false);
        await _store.SetMediaRepresentationAsync(work.Status.Id, resolved.Representation, _stop.Token).ConfigureAwait(false);
        work = work with { Status = work.Status with { Representation = resolved.Representation } };
        if (resolved.Redirect is not null)
        {
            resolved.Redirect.Validate();
            if (resolved.Redirect.Kind != "http") throw new InvalidDataException("解析重定向只能为直接 HTTP 来源。");
            return await DownloadHttpAsync(work, resolved.Redirect, _stop.Token).ConfigureAwait(false);
        }
        return await WriteBlobAsync(work, resolved.Content!, _stop.Token).ConfigureAwait(false);
    }

    /// <summary>响应头与正文使用独立期限，流式读取不设置整包总超时。</summary>
    private async Task<BlobResult> DownloadHttpAsync(MediaWork work, MediaSource source, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, source.Data.GetProperty("url").GetString());
        request.Headers.AcceptEncoding.ParseAdd("identity");
        HttpResponseMessage response;
        using (CancellationTokenSource deadline = new(_options.HeadersTimeout, _clock))
        using (CancellationTokenSource headers = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token))
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using Stream content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await WriteBlobAsync(work, content, token).ConfigureAwait(false);
        }
    }

    /// <summary>插件提供的流在有限额度内写入，不通过伪造消息获得媒体引用。</summary>
    public async Task<MediaStatus> ImportAsync(MediaKind kind, Stream content, JsonElement? representation = null, CancellationToken cancellationToken = default)
    {
        if (_pump is null) throw new InvalidOperationException("媒体服务尚未启动。");
        if (!content.CanRead || !Enum.IsDefined(kind)) throw new ArgumentException("导入媒体种类或输入流无效。");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitGate) { _stop.Token.ThrowIfCancellationRequested(); _imports.Add(completion.Task); }
        bool acquired = false;
        MediaWork? work = null;
        try
        {
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            work = await _store.CreateImportedMediaAsync(kind, representation?.Clone(), _clock.GetUtcNow(), linked.Token).ConfigureAwait(false);
            await WriteBlobAsync(work, content, linked.Token).ConfigureAwait(false);
            return (await _store.GetMediaAsync(work.Status.Id, linked.Token).ConfigureAwait(false))!.Status;
        }
        catch
        {
            if (work is not null)
            {
                MediaWork? current = await _store.GetMediaAsync(work.Status.Id, CancellationToken.None).ConfigureAwait(false);
                if (current?.Status.State == MediaState.Downloading)
                    await _store.FailMediaAsync(work.Status.Id, "媒体流导入未完成。", CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { if (acquired) _slots.Release(); lock (_waitGate) { completion.TrySetResult(); _imports.Remove(completion.Task); } }
    }

    /// <summary>所有来源共用流式摘要和文件提交日志，暂存文件不会被当作可用原件。</summary>
    private async Task<BlobResult> WriteBlobAsync(MediaWork work, Stream source, CancellationToken token)
    {
        string temporary = "media/staging/" + work.Status.Id.ToString("N") + ".part";
        bool prepared = false;
        try
        {
            long length = 0;
            string contentKey;
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (FileStream destination = new(PathFor(temporary), FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    // 每次读取独立计时；没有整包总时限，也不积累数据块历史。
                    while (true)
                    {
                        using CancellationTokenSource deadline = new(_options.ReadIdleTimeout, _clock);
                        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
                        int count = await source.ReadAsync(buffer.AsMemory(), read.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        digest.AppendData(buffer, 0, count);
                        await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        length += count;
                    }
                    await destination.FlushAsync(token).ConfigureAwait(false);
                    contentKey = Convert.ToHexStringLower(digest.GetHashAndReset());
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
            if (length == 0) throw new InvalidDataException("媒体响应为空。");
            string relative = "media/objects/" + contentKey[..2] + "/" + contentKey;
            await _store.SetMediaFinalizingAsync(work.Status.Id, contentKey, length, temporary, relative, token).ConfigureAwait(false);
            prepared = true;
            PlaceFile(temporary, relative, length);
            // 发布共享下载结果之前先完成拥有者，后来的来源查询才能复用。
            await _store.CompleteMediaAsync(work.Status.Id, contentKey, length, relative, token).ConfigureAwait(false);
            return new BlobResult { Key = contentKey, Length = length, Path = relative, Representation = work.Status.Representation };
        }
        finally
        {
            // 已登记的收尾操作留给恢复流程；只删除本任务尚未登记的部分文件。
            if (!prepared && File.Exists(PathFor(temporary))) File.Delete(PathFor(temporary));
        }
    }

    /// <summary>放置已登记原件；同内容竞争时复用既有目标，不覆盖它。</summary>
    private void PlaceFile(string temporary, string relative, long length)
    {
        string source = PathFor(temporary);
        string target = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            try { File.Move(source, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { }
        }
        if (new FileInfo(target).Length != length) throw new InvalidDataException("原件长度与提交记录不一致。");
        if (File.Exists(source)) File.Delete(source);
    }

    /// <summary>仅解析数据根目录内的相对路径，数据库记录也不能导致越界文件操作。</summary>
    private string PathFor(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("媒体存储路径无效。");
        string full = Path.GetFullPath(Path.Combine(_root, relative));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("媒体存储路径越界。");
        return full;
    }

    /// <summary>唤醒当前等待者；没有等待者的完成结果仍保存在数据库中。</summary>
    private void CompleteWaiters(Guid id, string? error)
    {
        lock (_waitGate)
        {
            if (_waiters.TryGetValue(id, out WaitSlot? slot)) slot.Completion.TrySetResult(error);
        }
        ArchiveCompleted?.Invoke();
    }

    /// <summary>状态无法持久化时明确使归档服务不可用，防止读取者无限等待。</summary>
    private void FaultArchive(Exception exception)
    {
        const string error = "媒体归档状态无法保存，归档服务已停止。";
        Volatile.Write(ref _failure, error);
        _logger.LogError("媒体归档基础设施失败：{ErrorType}", exception.GetType().Name);
        lock (_waitGate)
            foreach (WaitSlot slot in _waiters.Values) slot.Completion.TrySetResult(error);
        _stop.Cancel();
    }

    /// <summary>取消实际网络工作并等待文件句柄退出，不清理其他服务的文件。</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _wake.Writer.TryComplete();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        Task[] imports;
        lock (_waitGate) imports = _imports.ToArray();
        await Task.WhenAll(imports).ConfigureAwait(false);
    }

    /// <summary>回收本服务拥有的 HTTP 连接与取消源。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
        _stop.Dispose();
        _slots.Dispose();
        ArchiveCompleted = null;
    }

    /// <summary>等待数量归零即移除，不累计已完成媒体的 Task。</summary>
    private sealed class WaitSlot
    {
        public int Count { get; set; }
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>共享下载仅返回普通值，不共享文件流。</summary>
    private sealed record BlobResult
    {
        public required string Key { get; init; }
        public required long Length { get; init; }
        public required string Path { get; init; }
        public JsonElement? Representation { get; init; }
    }
}
