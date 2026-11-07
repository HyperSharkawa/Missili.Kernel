using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>独立实例的连接布局与业务入口，插件生命周期仍由 Nexus 所有。</summary>
internal sealed partial class OneBotAdapter : IAdapterDriver, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OneBotOptions _options;
    private readonly string? _token;
    private readonly IPluginContext _context;
    private readonly OneBotCodec _codec;
    private readonly Dictionary<string, OneBotSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _replacements = new(1, 1);
    private readonly SemaphoreSlim _requests;
    private readonly SemaphoreSlim _encoding;
    private readonly Channel<WirePayload> _events;
    private readonly Channel<Receipt> _receipts;
    private readonly ByteBudget _budget;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _temporaryDirectory;
    private bool? _online;

    /// <summary>准备实例队列和本任务暂存目录，构造时不连接远端。</summary>
    public OneBotAdapter(OneBotOptions options, string? token, IPluginContext context)
    {
        _options = options; _token = token; _context = context; _codec = new OneBotCodec(options);
        _requests = new SemaphoreSlim(options.MaxPendingRequests, options.MaxPendingRequests);
        _encoding = new SemaphoreSlim(options.MaxPendingRequests, options.MaxPendingRequests);
        _events = Channel.CreateBounded<WirePayload>(new BoundedChannelOptions(options.EventCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _receipts = Channel.CreateBounded<Receipt>(new BoundedChannelOptions(options.LateReceiptCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _budget = new ByteBudget(options.InboundByteBudget);
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "Missili.OneBot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>已激活的登记任务同时运行入站、迟到回执和所配置的正向连接。</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        using CancellationTokenRegistration registration = lifetime.Register(() => _stop.Cancel());
        List<Task> tasks = [ConsumeEventsAsync(), ConsumeReceiptsAsync()];
        if (_options.Direction == ConnectionDirection.Forward)
        {
            if (_options.Layout == ConnectionLayout.Universal) tasks.Add(ConnectLoopAsync("Universal", _options.Url!));
            else { tasks.Add(ConnectLoopAsync("API", _options.ApiUrl!)); tasks.Add(ConnectLoopAsync("Event", _options.EventUrl!)); }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>暴露事实状态，QQ 在线状态只有实际心跳或查询提供时才已知。</summary>
    public AdapterStatus GetStatus()
    {
        lock (_gate)
        {
            OneBotSession? api = ApiSession();
            OneBotSession? events = _sessions.GetValueOrDefault(_options.Layout == ConnectionLayout.Universal ? "Universal" : "Event");
            return new AdapterStatus
            {
                AdapterId = _options.Id,
                BotAccountId = _options.BotAccountId,
                PlatformId = "qq",
                Connected = _sessions.Count > 0,
                ApiAvailable = api is { Verified: true, CanCall: true },
                EventsAvailable = events is { Verified: true },
                IdentityVerified = api?.Verified == true || events?.Verified == true,
                Online = _online
            };
        }
    }

    /// <summary>固定角色循环使用有上限指数退避，身份验证成功后才重置退避。</summary>
    private async Task ConnectLoopAsync(string role, string url)
    {
        TimeSpan backoff = _options.ReconnectMinimum;
        while (!_stop.IsCancellationRequested)
        {
            bool verified = false;
            try
            {
                using ClientWebSocket socket = new();
                socket.Options.KeepAliveInterval = _options.KeepAliveInterval;
                socket.Options.KeepAliveTimeout = _options.KeepAliveTimeout;
                if (_token is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                using (CancellationTokenSource deadline = new(_options.ConnectTimeout, _context.Clock))
                using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stop.Token))
                    await socket.ConnectAsync(new Uri(url), connect.Token).ConfigureAwait(false);
                verified = await RunConnectionAsync(socket, role, reverse: false, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or OperationCanceledException or InvalidDataException)
            { _context.Logger.LogWarning("OneBot 实例 {AdapterId} 建连或身份核实失败：{ErrorType}", _options.Id, exception.GetType().Name); }
            if (_stop.IsCancellationRequested) break;
            if (verified) backoff = _options.ReconnectMinimum;
            double jitter = 1 + (Random.Shared.NextDouble() * 2 - 1) * _options.ReconnectJitter;
            try { await Task.Delay(TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * jitter), _context.Clock, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            if (!verified) backoff = TimeSpan.FromMilliseconds(Math.Min(_options.ReconnectMaximum.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }
    }

    /// <summary>升级前验证 Token、账号和布局；同角色替换通过全部检查后才影响旧连接。</summary>
    public async Task AcceptAsync(IWebSocketRequest request, CancellationToken cancellationToken)
    {
        if (_options.Direction != ConnectionDirection.Reverse || !request.IsWebSocketRequest) { await request.RejectAsync(400, cancellationToken).ConfigureAwait(false); return; }
        if (_token is not null)
        {
            if (!request.Headers.TryGetValue("Authorization", out string? authorization)) { await request.RejectAsync(401, cancellationToken).ConfigureAwait(false); return; }
            byte[] expected = Encoding.UTF8.GetBytes("Bearer " + _token), actual = Encoding.UTF8.GetBytes(authorization);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) { await request.RejectAsync(403, cancellationToken).ConfigureAwait(false); return; }
        }
        if (!request.Headers.TryGetValue("X-Self-ID", out string? identity) || identity != _options.BotAccountId)
        { await request.RejectAsync(403, cancellationToken).ConfigureAwait(false); return; }
        if (!request.Headers.TryGetValue("X-Client-Role", out string? role) || (_options.Layout == ConnectionLayout.Universal ? role != "Universal" : role is not ("API" or "Event")))
        { await request.RejectAsync(409, cancellationToken).ConfigureAwait(false); return; }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        using WebSocket socket = await request.AcceptAsync(_options.KeepAliveInterval, _options.KeepAliveTimeout, linked.Token).ConfigureAwait(false);
        await RunConnectionAsync(socket, role, reverse: true, linked.Token).ConfigureAwait(false);
    }

    /// <summary>发布新连接前串行替换角色，旧连接收尾不能移除新连接。</summary>
    private async Task<bool> RunConnectionAsync(WebSocket socket, string role, bool reverse, CancellationToken token)
    {
        OneBotSession session = new(socket, role, _options, _context.Clock, _context.Logger, _temporaryDirectory, _requests,
            EnqueueEventAsync, HandleMeta, EnqueueReceiptAsync);
        await _replacements.WaitAsync(token).ConfigureAwait(false);
        try
        {
            OneBotSession? previous;
            lock (_gate) previous = _sessions.GetValueOrDefault(role);
            if (previous is not null) { previous.RequestClose(); await previous.Completion.WaitAsync(token).ConfigureAwait(false); }
            lock (_gate) _sessions[role] = session;
        }
        finally { _replacements.Release(); }
        Task connection = session.RunAsync(token);
        try
        {
            if (role != "Event")
            {
                PlatformOperationResult login = await session.CallAsync("get_login_info", Empty(), null, _options.IdentityTimeout, token).ConfigureAwait(false);
                if (login.State != PlatformOperationState.Succeeded || login.Data is null || OneBotCodec.Identity(login.Data.Value, "user_id") != _options.BotAccountId)
                { session.RequestClose(); throw new InvalidDataException("登录账号与配置不一致。"); }
                session.Verified = true;
            }
            else session.Verified = reverse;
            _context.Adapters.NotifyChanged();
            await connection.ConfigureAwait(false);
            return session.Verified;
        }
        finally
        {
            session.RequestClose();
            await connection.ConfigureAwait(false);
            lock (_gate) { if (_sessions.GetValueOrDefault(role) == session) _sessions.Remove(role); }
            _context.Adapters.NotifyChanged();
        }
    }

    /// <summary>媒体与业务处理不在接收循环执行；队列容量和字节预算共同约束积压。</summary>
    private async Task EnqueueEventAsync(WirePayload payload, CancellationToken token)
    {
        await _budget.AcquireAsync(payload.Length, token).ConfigureAwait(false);
        try { await _events.Writer.WriteAsync(payload, token).ConfigureAwait(false); }
        catch { _budget.Release(payload.Length); throw; }
    }

    /// <summary>按接收顺序持久化业务，媒体归档和插件执行由宿主独立调度。</summary>
    private async Task ConsumeEventsAsync()
    {
        try
        {
            await foreach (WirePayload payload in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                long bytes = payload.Length;
                await using (payload)
                {
                    try
                    {
                        using JsonDocument document = await payload.ParseAsync(_options.JsonMaxDepth, _stop.Token).ConfigureAwait(false);
                        JsonElement root = document.RootElement;
                        _codec.VerifyIdentity(root);
                        string type = OneBotCodec.String(root, "post_type");
                        if (type is "message" or "message_sent")
                        {
                            ReceiveReceipt receipt = await _context.Ingress.ReceiveAsync(_codec.DecodeMessage(root), _stop.Token).ConfigureAwait(false);
                            if (receipt.Error is not null) _context.Logger.LogError("OneBot 消息保存失败，未触发处理");
                        }
                        else if (type is "notice" or "request")
                        {
                            EventReceipt receipt = await _context.EventIngress.ReceiveEventAsync(_codec.DecodeEvent(root), _stop.Token).ConfigureAwait(false);
                            if (receipt.Error is not null) _context.Logger.LogError("OneBot 事件保存失败，未触发处理");
                        }
                        else _context.Logger.LogWarning("OneBot 上报类型未支持");
                        lock (_gate) { if (_sessions.TryGetValue("Event", out OneBotSession? events)) events.Verified = true; }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                    { _context.Logger.LogWarning("OneBot 业务事件被拒绝：{ErrorType}", exception.GetType().Name); }
                    finally { _budget.Release(bytes); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { while (_events.Reader.TryRead(out WirePayload? payload)) { _budget.Release(payload.Length); await payload.DisposeAsync().ConfigureAwait(false); } }
    }

    /// <summary>业务心跳只更新运行状态，不创建长期事件历史。</summary>
    private void HandleMeta(JsonElement root)
    {
        _codec.VerifyIdentity(root);
        if (OneBotCodec.String(root, "meta_event_type") == "heartbeat" && root.TryGetProperty("status", out JsonElement status) && status.TryGetProperty("online", out JsonElement online))
            lock (_gate) _online = online.ValueKind == JsonValueKind.Null ? null : online.GetBoolean();
    }

    /// <summary>将迟到回执交给有限队列，接收循环不等待数据库写入。</summary>
    private Task EnqueueReceiptAsync(long id, TransportResult result, CancellationToken token) =>
        _receipts.Writer.WriteAsync(new Receipt { Id = id, Result = result }, token).AsTask();

    /// <summary>异步保存有限迟到确认；不再次发送网络请求。</summary>
    private async Task ConsumeReceiptsAsync()
    {
        try
        {
            await foreach (Receipt receipt in _receipts.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (receipt.Result.State is DeliveryState.Unknown or DeliveryState.Cancelled) continue;
                SendResult result = await _context.Receipts.RecordReceiptAsync(receipt.Id, receipt.Result, _stop.Token).ConfigureAwait(false);
                if (!result.Recorded) _context.Logger.LogError("发送 {MessageId} 的迟到回执记录失败", receipt.Id);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>普通操作只使用当前已核实连接；离线不等待重连。</summary>
    public Task<PlatformOperationResult> InvokeAsync(string action, JsonElement parameters, CancellationToken cancellationToken)
    {
        OneBotSession? session;
        lock (_gate) session = ApiSession();
        if (session is not { Verified: true, CanCall: true }) return Task.FromResult(new PlatformOperationResult { State = PlatformOperationState.Unavailable, Error = "API 通道离线。" });
        return session.CallAsync(action, parameters, null, _options.RequestTimeout, cancellationToken);
    }

    /// <summary>必须在连接锁内读取所配置的 API 角色，不能在布局间猜测回退。</summary>
    private OneBotSession? ApiSession() => _sessions.GetValueOrDefault(_options.Layout == ConnectionLayout.Universal ? "Universal" : "API");
    /// <summary>创建共享空参数，避免插件私有类型进入全局序列化缓存。</summary>
    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new Dictionary<string, string>());

    /// <summary>请求停止所有连接，实际结束由登记任务与调用资格跟踪。</summary>
    public Task StopAsync()
    {
        lock (_gate) foreach (OneBotSession session in _sessions.Values) session.RequestClose();
        return _stop.CancelAsync();
    }

    /// <summary>只有 Nexus 等待任务退出后才释放资源，临时目录非空时不递归删除未知文件。</summary>
    public ValueTask DisposeAsync()
    {
        _requests.Dispose(); _encoding.Dispose(); _replacements.Dispose(); _stop.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>迟到回执队列仅保存普通发送标识和结果。</summary>
    private sealed record Receipt { public long Id { get; init; } public required TransportResult Result { get; init; } }
}
