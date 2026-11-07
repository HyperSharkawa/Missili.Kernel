using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>当前已发布的适配器路由，只借用 Nexus 代次资格，不拥有第二套启停状态。</summary>
public sealed class AdapterDirectory : IAdapterRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();
    private long _revision;
    public long Revision { get { lock (_gate) return _revision; } }

    /// <summary>一次检查并发布同代次全部注册，冲突不会留下部分可见能力。</summary>
    internal void Publish(PluginGeneration owner, IReadOnlyList<AdapterRegistration> registrations)
    {
        lock (_gate)
        {
            if (registrations.Any(item => _entries.ContainsKey(item.AdapterId))) throw new InvalidOperationException("适配器标识已由另一加载代次注册。");
            foreach (AdapterRegistration registration in registrations) _entries.Add(registration.AdapterId, new Entry(owner, registration));
            Pulse();
        }
    }

    /// <summary>停止时先撤销目录可见性；已经取得的调用仍由原代次等待退出。</summary>
    internal void Withdraw(Guid generation)
    {
        lock (_gate)
        {
            string[] ids = _entries.Where(item => item.Value.Owner.Id == generation).Select(item => item.Key).ToArray();
            foreach (string id in ids) _entries.Remove(id);
            Pulse();
        }
    }

    /// <summary>网络状态变化唤醒宿主等待，不在锁内执行插件代码。</summary>
    internal void NotifyChanged() { lock (_gate) Pulse(); }

    /// <summary>无轮询地等待目录修订变化，调用者取消不影响其他等待者。</summary>
    public Task WaitForChangeAsync(long revision, CancellationToken cancellationToken)
    { lock (_gate) return revision != _revision ? Task.CompletedTask : _changed.Task.WaitAsync(cancellationToken); }

    /// <summary>捕获活动代次状态，返回值不包含驱动实例或委托。</summary>
    public ImmutableArray<AdapterStatus> GetStatuses()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        ImmutableArray<AdapterStatus>.Builder result = ImmutableArray.CreateBuilder<AdapterStatus>();
        foreach (Entry entry in entries)
        {
            using IHandlerLease? lease = entry.Owner.TryAcquireCapability();
            if (lease is null) continue;
            result.Add(entry.Registration.Driver.GetStatus() with
            { AdapterId = entry.Registration.AdapterId, BotAccountId = entry.Registration.BotAccountId, PlatformId = entry.Registration.PlatformId, Generation = entry.Owner.Id });
        }
        return result.ToImmutable();
    }

    /// <summary>普通平台调用禁止标准发送及其修饰形式，发送始终经过意图记录。</summary>
    public async Task<PlatformOperationResult> InvokeAsync(string adapterId, string botAccountId, string action, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        string baseAction = action;
        while (baseAction.EndsWith("_async", StringComparison.Ordinal) || baseAction.EndsWith("_rate_limited", StringComparison.Ordinal))
            baseAction = baseAction.EndsWith("_async", StringComparison.Ordinal) ? baseAction[..^6] : baseAction[..^13];
        if ((baseAction.StartsWith("send_", StringComparison.Ordinal) && baseAction != "send_like") || baseAction == ".handle_quick_operation")
            throw new ArgumentException("消息发送操作必须使用 IMessageSender。");
        if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("平台操作参数必须为 JSON 对象。");
        using Call? call = Acquire(adapterId, botAccountId);
        if (call is null) return new PlatformOperationResult { State = PlatformOperationState.Unavailable, Error = "适配器暂不可用。" };
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Lease.LifetimeToken);
        return (await call.Driver.InvokeAsync(action, parameters.Clone(), linked.Token).ConfigureAwait(false)).Freeze();
    }

    /// <summary>为一次发送取得当前资格；失去连接由驱动返回明确结果，不重定向到新代次。</summary>
    public async Task<TransportResult> SendAsync(long id, OutgoingMessage message, CancellationToken cancellationToken)
    {
        using Call? call = Acquire(message.Address.AdapterId, message.Address.BotAccountId);
        if (call is null) return new TransportResult { State = DeliveryState.Failed, Error = "目标适配器尚未注册或正在重载，暂不可用。" };
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Lease.LifetimeToken);
        return await call.Driver.SendAsync(id, message, linked.Token).ConfigureAwait(false);
    }

    /// <summary>固定宿主路由在整条连接期间持有资格，返回才表示请求处理真正结束。</summary>
    public async Task AcceptAsync(string adapterId, IWebSocketRequest request, CancellationToken cancellationToken)
    {
        using Call? call = Acquire(adapterId, null);
        if (call is null || !call.Entry.Registration.ReverseWebSocket)
        { await request.RejectAsync(404, cancellationToken).ConfigureAwait(false); return; }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Lease.LifetimeToken);
        await call.Driver.AcceptAsync(request, linked.Token).ConfigureAwait(false);
    }

    /// <summary>流的实际释放才结束解析调用，重载不会提前丢失仍在读取的插件代码。</summary>
    public Task<MediaSourceRead> ResolveMediaAsync(MediaSource source, CancellationToken cancellationToken) => ReadMediaAsync(source, null, cancellationToken);

    /// <summary>转换请求复用已有流资格包装，取消后等待插件真正停止读取。</summary>
    public Task<MediaSourceRead> ResolveRepresentationAsync(MediaSource source, string format, CancellationToken cancellationToken)
    { ArgumentException.ThrowIfNullOrWhiteSpace(format); return ReadMediaAsync(source, format, cancellationToken); }

    /// <summary>普通获取与格式转换共享代次、错误和流释放语义。</summary>
    private async Task<MediaSourceRead> ReadMediaAsync(MediaSource source, string? format, CancellationToken cancellationToken)
    {
        Call call = Acquire(source.AdapterId!, source.BotAccountId) ?? throw new AdapterUnavailableException();
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Lease.LifetimeToken);
        try
        {
            MediaSourceRead result = format is null ? await call.Driver.ResolveMediaAsync(source, linked.Token).ConfigureAwait(false)
                : await call.Driver.ResolveRepresentationAsync(source, format, linked.Token).ConfigureAwait(false);
            if ((result.Content is null) == (result.Redirect is null))
            { await result.DisposeAsync().ConfigureAwait(false); throw new InvalidDataException("媒体解析必须返回流或直接来源之一。"); }
            if (result.Content is not null)
                return new MediaSourceRead { Content = new LeasedReadStream(result.Content, call, linked), Representation = result.Representation?.Clone() };
            MediaSourceRead redirect = new() { Redirect = result.Redirect!.Freeze(), Representation = result.Representation?.Clone() };
            call.Dispose(); linked.Dispose();
            return redirect;
        }
        catch (OperationCanceledException) when (call.Lease.LifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { call.Dispose(); linked.Dispose(); throw new AdapterUnavailableException(); }
        catch { call.Dispose(); linked.Dispose(); throw; }
    }

    /// <summary>结构化归档调用不占消息处理额度，避免插件内部查询自己造成循环等待。</summary>
    public async Task ExpandContentAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        using Call call = Acquire(current.Source.Address.AdapterId, current.Source.Address.BotAccountId) ?? throw new AdapterUnavailableException();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Lease.LifetimeToken);
        try { await call.Driver.ExpandContentAsync(current, save, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (call.Lease.LifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new AdapterUnavailableException(); }
    }

    /// <summary>先取目录快照再取得原代次资格，绝不持有目录锁调用插件或代次锁。</summary>
    private Call? Acquire(string adapterId, string? botAccountId)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(adapterId, out entry);
        if (entry is null || (botAccountId is not null && entry.Registration.BotAccountId != botAccountId)) return null;
        IHandlerLease? lease = entry.Owner.TryAcquireCapability();
        return lease is null ? null : new Call(entry, lease);
    }

    /// <summary>在目录锁内替换信号，异步续体避免重入目录操作。</summary>
    private void Pulse()
    { _revision++; TaskCompletionSource old = _changed; _changed = NewSignal(); old.TrySetResult(); }
    /// <summary>所有等待共享一次变化信号，不为每个来源建立回调。</summary>
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>仅当前发布期间持有代次与驱动，不保存卸载历史。</summary>
    private sealed record Entry
    {
        public PluginGeneration Owner { get; }
        public AdapterRegistration Registration { get; }
        /// <summary>保存同一代次的登记。</summary>
        public Entry(PluginGeneration owner, AdapterRegistration registration) { Owner = owner; Registration = registration; }
    }

    /// <summary>一次平台调用借用代次，退出后即归还资格。</summary>
    private sealed class Call : IDisposable
    {
        private Entry? _entry;
        private IHandlerLease? _lease;
        public Entry Entry => _entry ?? throw new ObjectDisposedException(nameof(Call));
        public IHandlerLease Lease => _lease ?? throw new ObjectDisposedException(nameof(Call));
        public IAdapterDriver Driver => Entry.Registration.Driver;
        /// <summary>绑定已登记资格与本次实现。</summary>
        public Call(Entry entry, IHandlerLease lease) { _entry = entry; _lease = lease; }
        /// <summary>幂等归还资格，不改变插件接单状态。</summary>
        public void Dispose() { _entry = null; Interlocked.Exchange(ref _lease, null)?.Dispose(); }
    }

    /// <summary>只读流持有插件资格和取消源，读取期间卸载必须等待实际释放。</summary>
    private sealed class LeasedReadStream : Stream
    {
        private Stream? _stream;
        private Call? _call;
        private readonly CancellationTokenSource _lifetime;
        private Stream Inner => _stream ?? throw new ObjectDisposedException(nameof(LeasedReadStream));
        public override bool CanRead => _stream?.CanRead ?? false;
        public override bool CanSeek => _stream?.CanSeek ?? false;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        /// <summary>取得返回流及代次资格的释放责任。</summary>
        public LeasedReadStream(Stream stream, Call call, CancellationTokenSource lifetime) { _stream = stream; _call = call; _lifetime = lifetime; }
        /// <summary>同步读取也检查代次取消，不共享其他调用的流位置。</summary>
        public override int Read(byte[] buffer, int offset, int count) { _lifetime.Token.ThrowIfCancellationRequested(); return Inner.Read(buffer, offset, count); }
        /// <summary>异步读取同时响应读取者和加载代次取消。</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try { return await Inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw new AdapterUnavailableException(); }
        }
        /// <summary>数组异步入口采用同一取消和归属规则。</summary>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        /// <summary>仅转发可读原件的定位能力。</summary>
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        /// <summary>只读流没有写缓存。</summary>
        public override void Flush() { }
        /// <summary>拒绝修改原件。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>拒绝写入原件。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <summary>先释放插件流再归还资格，使关闭不会提前宣布卸载完成。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _stream is not null) { _stream.Dispose(); _stream = null; _call!.Dispose(); _call = null; _lifetime.Dispose(); }
            base.Dispose(disposing);
        }
        /// <summary>异步释放成功后才解除代次引用。</summary>
        public override async ValueTask DisposeAsync()
        {
            if (_stream is null) return;
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null; _call!.Dispose(); _call = null; _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
