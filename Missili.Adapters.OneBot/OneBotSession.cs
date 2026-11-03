using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>单连接的接收、串行写入与请求关联；连接代次结束后请求不会迁往其他连接。</summary>
internal sealed class OneBotSession
{
    private readonly object _gate = new();
    private readonly WebSocket _socket;
    private readonly OneBotOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly string _temporaryDirectory;
    private readonly Func<WirePayload, CancellationToken, Task> _onEvent;
    private readonly Action<JsonElement> _onMeta;
    private readonly Func<long, TransportResult, CancellationToken, Task> _onLateReceipt;
    private readonly SemaphoreSlim _requestSlots;
    private readonly Channel<Outbound> _outbound;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LateReceipt> _late = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _closeRequested = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private long _sequence;
    private bool _closing;
    public string Role { get; }
    public bool Verified { get; set; }
    public Task Completion => _finished.Task;
    public bool CanCall { get { lock (_gate) return !_closing && Role != "Event"; } }

    /// <summary>为同一实例共享请求额度，每条连接独立关联 echo 和发送队列。</summary>
    public OneBotSession(WebSocket socket, string role, OneBotOptions options, TimeProvider clock, ILogger logger,
        string temporaryDirectory, SemaphoreSlim requestSlots, Func<WirePayload, CancellationToken, Task> onEvent,
        Action<JsonElement> onMeta, Func<long, TransportResult, CancellationToken, Task> onLateReceipt)
    {
        _socket = socket; Role = role; _options = options; _clock = clock; _logger = logger; _temporaryDirectory = temporaryDirectory;
        _requestSlots = requestSlots; _onEvent = onEvent; _onMeta = onMeta; _onLateReceipt = onLateReceipt;
        _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(options.SendCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    /// <summary>启动一个接收和一个写入循环，停止时给关闭握手限定期限。</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        Task send = SendLoopAsync();
        Task shutdown = WatchShutdownAsync(lifetime);
        try { await ReceiveLoopAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (WebSocketException) { _logger.LogInformation("OneBot 连接结束，未完成调用保留未知状态"); }
        catch (InvalidDataException exception) { _logger.LogWarning("OneBot 协议输入被拒绝：{Reason}", exception.Message); }
        finally
        {
            lock (_gate) _closing = true;
            await _stop.CancelAsync().ConfigureAwait(false);
            _outbound.Writer.TryComplete();
            await send.ConfigureAwait(false);
            await shutdown.ConfigureAwait(false);
            FinishPending();
            // 写入循环已经退出后才允许另一处发送关闭控制帧。
            if (_socket.State == WebSocketState.CloseReceived)
            {
                using CancellationTokenSource close = new(_options.CloseTimeout, _clock);
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", close.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { _socket.Abort(); }
            }
            _socket.Dispose(); _finished.TrySetResult();
        }
    }

    /// <summary>实际传输前登记请求；超时和取消只结束此请求，不进行重发。</summary>
    public async Task<PlatformOperationResult> CallAsync(string action, JsonElement parameters, long? messageId, TimeSpan timeout, CancellationToken token)
    {
        if (!CanCall) return Unavailable();
        using CancellationTokenSource deadline = new(timeout, _clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, _stop.Token);
        bool admitted = false;
        Pending? pending = null;
        string echo = _options.Id + ":" + _generation + ":" + Interlocked.Increment(ref _sequence);
        try
        {
            await _requestSlots.WaitAsync(linked.Token).ConfigureAwait(false); admitted = true;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["action"] = action, ["params"] = parameters, ["echo"] = echo });
            if (payload.Length > _options.MaxMessageBytes) return new PlatformOperationResult { State = PlatformOperationState.Failed, Error = "编码后发送载荷超过上限。" };
            pending = new Pending { MessageId = messageId };
            lock (_gate) { if (_closing) return Unavailable(); _pending.Add(echo, pending); }
            await _outbound.Writer.WriteAsync(new Outbound { Echo = echo, Payload = payload, Request = pending }, linked.Token).ConfigureAwait(false);
            return await pending.Result.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (pending?.Result.Task.IsCompletedSuccessfully == true) return pending.Result.Task.Result;
                if (pending is not null)
                {
                    _pending.Remove(echo); pending.Ended = true;
                    if (pending.Started && messageId.HasValue && !_closing) RememberLate(echo, messageId.Value);
                }
                return new PlatformOperationResult
                {
                    State = pending?.Started == true ? PlatformOperationState.Unknown
                    : token.IsCancellationRequested ? PlatformOperationState.Cancelled : PlatformOperationState.Unavailable,
                    Error = pending?.Started == true ? "调用已开始，未取得确定回执。" : "调用尚未传输即结束。"
                };
            }
        }
        catch (ChannelClosedException) { return pending?.Started == true ? new PlatformOperationResult { State = PlatformOperationState.Unknown } : Unavailable(); }
        finally
        {
            lock (_gate) { _pending.Remove(echo); if (pending is not null) pending.Ended = true; }
            if (admitted) _requestSlots.Release();
        }
    }

    /// <summary>发起关闭而不等待自身接收循环，角色替换可安全要求旧连接退出。</summary>
    public void RequestClose()
    {
        lock (_gate) { if (_closing) return; _closing = true; }
        _closeRequested.Cancel();
        if (!_outbound.Writer.TryWrite(new Outbound())) _stop.Cancel();
    }

    /// <summary>宿主或代次取消后请求握手，超时才中断底层接收。</summary>
    private async Task WatchShutdownAsync(CancellationToken lifetime)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _stop.Token, _closeRequested.Token);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        if (_stop.IsCancellationRequested) return;
        RequestClose();
        try { await Task.Delay(_options.CloseTimeout, _clock, _stop.Token).ConfigureAwait(false); _stop.Cancel(); _socket.Abort(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>回执直接完成等待，业务帧仅交给有界队列，元事件不持久化。</summary>
    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            WirePayload? payload = await WirePayload.ReceiveAsync(_socket, _options, _temporaryDirectory, _stop.Token).ConfigureAwait(false);
            if (payload is null) return;
            bool transferred = false;
            try
            {
                using JsonDocument document = await payload.ParseAsync(_options.JsonMaxDepth, _stop.Token).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("OneBot 根值必须为对象。");
                if (root.TryGetProperty("echo", out JsonElement echo) && !root.TryGetProperty("post_type", out _))
                { await HandleResponseAsync(root, echo).ConfigureAwait(false); continue; }
                if (root.TryGetProperty("post_type", out JsonElement post) && post.ValueKind == JsonValueKind.String && post.GetString() == "meta_event")
                { _onMeta(root); continue; }
                if (Role == "API") { _logger.LogWarning("API 专用连接收到业务事件，已拒绝该帧"); continue; }
                document.Dispose();
                await _onEvent(payload, _stop.Token).ConfigureAwait(false); transferred = true;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { _logger.LogWarning("OneBot 帧解析失败：{ErrorType}", exception.GetType().Name); }
            finally { if (!transferred) await payload.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>同一连接最多一个写入；取消且未开始的请求不会进入网络。</summary>
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (Outbound work in _outbound.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (work.Request is null)
                { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stopping", _stop.Token).ConfigureAwait(false); return; }
                lock (_gate) { if (work.Request.Ended) continue; work.Request.Started = true; }
                await _socket.SendAsync(work.Payload!.AsMemory(), WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (WebSocketException) { _stop.Cancel(); }
        finally { while (_outbound.Reader.TryRead(out _)) { } }
    }

    /// <summary>只有当前连接的字符串 echo 可以完成请求，其他回执只记诊断。</summary>
    private async Task HandleResponseAsync(JsonElement root, JsonElement echoValue)
    {
        if (Role == "Event" || echoValue.ValueKind != JsonValueKind.String) { _logger.LogWarning("忽略角色或 echo 类型不匹配的回执"); return; }
        string echo = echoValue.GetString()!;
        PlatformOperationResult result = ParseResponse(root);
        LateReceipt? late = null;
        lock (_gate)
        {
            PruneLate();
            if (_pending.Remove(echo, out Pending? pending))
            {
                pending.Ended = true;
                if (result.State == PlatformOperationState.Accepted && pending.MessageId.HasValue) RememberLate(echo, pending.MessageId.Value);
                pending.Result.TrySetResult(result); return;
            }
            _late.Remove(echo, out late);
        }
        if (late is not null) await _onLateReceipt(late.MessageId, ToSendResult(result), _stop.Token).ConfigureAwait(false);
        else _logger.LogDebug("忽略未知或已过期的 OneBot 回执");
    }

    /// <summary>标准状态与 retcode 必须一致，格式错误不能冒充确认成功。</summary>
    internal static PlatformOperationResult ParseResponse(JsonElement root)
    {
        try
        {
            string status = OneBotCodec.String(root, "status");
            int retcode = root.GetProperty("retcode").GetInt32();
            PlatformOperationState state = status switch
            {
                "ok" when retcode == 0 => PlatformOperationState.Succeeded,
                "async" when retcode == 1 => PlatformOperationState.Accepted,
                "failed" when retcode is not (0 or 1) => PlatformOperationState.Failed,
                _ => PlatformOperationState.Unknown
            };
            return new PlatformOperationResult
            {
                State = state,
                Status = status,
                Retcode = retcode,
                Data = root.TryGetProperty("data", out JsonElement data) ? data.Clone() : null,
                Error = state == PlatformOperationState.Failed ? OneBotCodec.OptionalString(root, "message") ?? OneBotCodec.OptionalString(root, "wording") ?? "OneBot 返回失败，retcode=" + retcode : state == PlatformOperationState.Unknown ? "OneBot 回执状态不符合标准。" : null
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException)
        { return new PlatformOperationResult { State = PlatformOperationState.Unknown, Error = "OneBot 回执结构无效。" }; }
    }

    /// <summary>发送成功还必须具有有效平台消息标识，异步受理保留未确认状态。</summary>
    internal static TransportResult ToSendResult(PlatformOperationResult result)
    {
        if (result.State == PlatformOperationState.Succeeded)
        {
            try { return new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = OneBotCodec.Identity(result.Data!.Value, "message_id") }; }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or InvalidDataException)
            { return new TransportResult { State = DeliveryState.Unknown, Error = "成功响应缺少有效消息标识。" }; }
        }
        return new TransportResult
        {
            State = result.State switch
            {
                PlatformOperationState.Failed or PlatformOperationState.Unavailable => DeliveryState.Failed,
                PlatformOperationState.Cancelled => DeliveryState.Cancelled,
                PlatformOperationState.Accepted => DeliveryState.Accepted,
                _ => DeliveryState.Unknown
            },
            Error = result.Error
        };
    }

    /// <summary>超时请求只保留有限的纯值关联，绝不保存插件委托。</summary>
    private void RememberLate(string echo, long id)
    {
        PruneLate();
        while (_late.Count >= _options.LateReceiptCapacity) _late.Remove(_late.MinBy(item => item.Value.Expires).Key);
        _late[echo] = new LateReceipt { MessageId = id, Expires = _clock.GetUtcNow() + _options.LateReceiptRetention };
    }
    /// <summary>根据可控时钟移除已过期的迟到关联。</summary>
    private void PruneLate() { foreach (string key in _late.Where(item => item.Value.Expires <= _clock.GetUtcNow()).Select(item => item.Key).ToArray()) _late.Remove(key); }
    /// <summary>连接结束时区分已经开始和仍在队列中的调用，并清除关联表。</summary>
    private void FinishPending()
    {
        lock (_gate)
        {
            foreach (Pending pending in _pending.Values)
            { pending.Ended = true; pending.Result.TrySetResult(pending.Started ? new PlatformOperationResult { State = PlatformOperationState.Unknown, Error = "连接已断开，调用结果未知。" } : Unavailable()); }
            _pending.Clear(); _late.Clear();
        }
    }
    /// <summary>离线是即时不可用，不暗示可以跨重连自动补发。</summary>
    private static PlatformOperationResult Unavailable() => new() { State = PlatformOperationState.Unavailable, Error = "API 连接不可用。" };

    /// <summary>单次关联状态只在连接锁内切换，实际完成通过异步信号通知。</summary>
    private sealed class Pending
    {
        public long? MessageId { get; init; }
        public bool Started { get; set; }
        public bool Ended { get; set; }
        public TaskCompletionSource<PlatformOperationResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    /// <summary>一项串行写入，空请求表示关闭控制帧。</summary>
    private sealed class Outbound
    {
        public string? Echo { get; init; }
        public byte[]? Payload { get; init; }
        public Pending? Request { get; init; }
    }
    /// <summary>迟到回执只持有本地发送标识和到期时间。</summary>
    private sealed class LateReceipt
    {
        public long MessageId { get; init; }
        public DateTimeOffset Expires { get; init; }
    }
}
