using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Missili.OneBot.Mock;

/// <summary>真实 WebSocket 对端，可作为正向服务端或反向客户端，完全不引用生产协议编码器。</summary>
public sealed class MockPeer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly List<Task> _clients = [];
    private readonly Channel<MockAction> _actions = Channel.CreateBounded<MockAction>(256);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _server;
    public long SelfId { get; set; } = 10001;
    public string? Token { get; set; }
    public string? BaseUrl { get; private set; }
    public bool AutoRespond { get; set; } = true;
    public ConcurrentQueue<JsonElement> RecordedActions { get; } = new();
    public Func<MockAction, CancellationToken, Task<bool>>? ResponseHandler { get; set; }
    public byte[] Media { get; set; } = [1, 2, 3, 4, 5];

    /// <summary>启动回环协议服务和合成媒体端点，端口可由系统分配。</summary>
    public async Task StartServerAsync(string url = "http://127.0.0.1:0", CancellationToken token = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls(url);
        _server = builder.Build(); _server.UseWebSockets();
        _server.MapGet("/media", () => Results.Bytes(Media, "application/octet-stream"));
        _server.Map("/", context => AcceptAsync(context, "Universal"));
        _server.Map("/api", context => AcceptAsync(context, "API"));
        _server.Map("/event", context => AcceptAsync(context, "Event"));
        await _server.StartAsync(token).ConfigureAwait(false);
        BaseUrl = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    /// <summary>按明确角色建立反向连接，Header 使用标准格式。</summary>
    public async Task ConnectAsync(string url, string role, CancellationToken token = default)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("X-Self-ID", SelfId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        socket.Options.SetRequestHeader("X-Client-Role", role);
        if (Token is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        try { await socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false); }
        catch { socket.Dispose(); throw; }
        Task run = RunConnectionAsync(socket, role);
        lock (_gate) _clients.Add(run);
    }

    /// <summary>等待指定角色真实完成握手；不靠固定休眠判断连接可用。</summary>
    public async Task WaitConnectedAsync(string role, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_gate) { if (_connections.ContainsKey(role)) return; changed = _changed.Task; }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>发送原始合成事件，分片大小用于覆盖 UTF-8 和 JSON 边界。</summary>
    public async Task SendAsync(string role, string json, int fragmentBytes = int.MaxValue, CancellationToken token = default)
    {
        Connection connection = Get(role);
        await connection.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, fragmentBytes, token).ConfigureAwait(false);
    }

    /// <summary>发送二进制异常输入，验证接收方协议拒绝。</summary>
    public Task SendBinaryAsync(string role, byte[] bytes, CancellationToken token = default) => Get(role).SendAsync(bytes, WebSocketMessageType.Binary, int.MaxValue, token);
    /// <summary>等待实际收到操作，测试可以控制回执顺序或故意不回执。</summary>
    public Task<MockAction> NextActionAsync(CancellationToken token) => _actions.Reader.ReadAsync(token).AsTask();
    /// <summary>强制断开指定角色，模拟请求已提交但确认丢失。</summary>
    public void Abort(string role) => Get(role).Socket.Abort();

    /// <summary>按独立样例生成标准群消息，不依赖被测编解码器。</summary>
    public string Message(long id, string text, bool group = true) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["time"] = 1700000000L,
        ["self_id"] = SelfId,
        ["post_type"] = "message",
        ["message_type"] = group ? "group" : "private",
        ["sub_type"] = group ? "normal" : "friend",
        ["message_id"] = id,
        ["group_id"] = 20001L,
        ["user_id"] = 30001L,
        ["message"] = new[] { new Dictionary<string, object> { ["type"] = "text", ["data"] = new Dictionary<string, string> { ["text"] = text } } },
        ["raw_message"] = text,
        ["font"] = 0,
        ["sender"] = new Dictionary<string, object> { ["user_id"] = 30001L, ["nickname"] = "合成用户" }
    });

    /// <summary>升级前鉴权，缺失和错误 Token 分别使用标准拒绝状态。</summary>
    private async Task AcceptAsync(HttpContext context, string role)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (Token is not null && context.Request.Headers.Authorization.ToString() != "Bearer " + Token)
        { context.Response.StatusCode = context.Request.Headers.ContainsKey("Authorization") ? 403 : 401; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await RunConnectionAsync(socket, role).ConfigureAwait(false);
    }

    /// <summary>接收 API 调用并保存合成证据，可由场景回调决定何时回复。</summary>
    private async Task RunConnectionAsync(WebSocket socket, string role)
    {
        Connection connection = new(socket);
        lock (_gate) { _connections[role] = connection; Pulse(); }
        try
        {
            byte[] buffer = new byte[65536];
            while (!_stop.IsCancellationRequested)
            {
                using MemoryStream payload = new();
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    { await connection.CloseAsync(_stop.Token).ConfigureAwait(false); return; }
                    if (payload.Length + frame.Count > 64L * 1024 * 1024) throw new InvalidDataException("Mock 接收超过上限。");
                    await payload.WriteAsync(buffer.AsMemory(0, frame.Count), _stop.Token).ConfigureAwait(false);
                } while (!frame.EndOfMessage);
                using JsonDocument document = JsonDocument.Parse(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)));
                JsonElement request = document.RootElement.Clone();
                MockAction action = new(request, (json, cancellation) => connection.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, int.MaxValue, cancellation));
                RecordedActions.Enqueue(request);
                await _actions.Writer.WriteAsync(action, _stop.Token).ConfigureAwait(false);
                if (ResponseHandler is not null && await ResponseHandler(action, _stop.Token).ConfigureAwait(false)) continue;
                if (AutoRespond) await RespondDefaultAsync(action, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || socket.State == WebSocketState.Aborted) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_gate) { if (_connections.GetValueOrDefault(role) == connection) _connections.Remove(role); Pulse(); }
            socket.Dispose();
        }
    }

    /// <summary>最小标准身份、状态和发送响应，其他接口明确报告未实现。</summary>
    private Task RespondDefaultAsync(MockAction action, CancellationToken token)
    {
        object? data = action.Action switch
        {
            "get_login_info" => new Dictionary<string, object> { ["user_id"] = SelfId, ["nickname"] = "Mock" },
            "get_status" => new Dictionary<string, object> { ["online"] = true, ["good"] = true },
            "send_group_msg" or "send_private_msg" or "send_msg" => new Dictionary<string, object> { ["message_id"] = RecordedActions.Count },
            "delete_msg" or "set_friend_add_request" or "set_group_add_request" => null,
            _ => null
        };
        bool supported = data is not null || action.Action is "delete_msg" or "set_friend_add_request" or "set_group_add_request";
        return action.RespondAsync(supported ? "ok" : "failed", supported ? 0 : 1404, JsonSerializer.SerializeToElement(data), token);
    }

    /// <summary>目录变化信号只服务测试同步，不承担生产调度。</summary>
    private void Pulse() { TaskCompletionSource changed = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); changed.TrySetResult(); }
    /// <summary>读取当前明确角色，不自动选择另一连接。</summary>
    private Connection Get(string role) { lock (_gate) return _connections.TryGetValue(role, out Connection? connection) ? connection : throw new InvalidOperationException("Mock 角色尚未连接。"); }

    /// <summary>停止本 Mock 创建的连接和本地服务器，保留其他进程。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        lock (_gate) foreach (Connection connection in _connections.Values) connection.Socket.Abort();
        Task[] clients; lock (_gate) clients = _clients.ToArray();
        await Task.WhenAll(clients).ConfigureAwait(false);
        if (_server is not null) { await _server.StopAsync().ConfigureAwait(false); await _server.DisposeAsync().ConfigureAwait(false); }
        _stop.Dispose();
    }

    /// <summary>Mock 发送也遵守同一连接不能并发写入的 WebSocket 约束。</summary>
    private sealed class Connection
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        public WebSocket Socket { get; }
        /// <summary>取得此条连接的发送串行责任。</summary>
        public Connection(WebSocket socket) => Socket = socket;
        /// <summary>按指定片段发送原始字节，保持中途 UTF-8 分片。</summary>
        public async Task SendAsync(byte[] bytes, WebSocketMessageType type, int fragmentBytes, CancellationToken token)
        {
            await _write.WaitAsync(token).ConfigureAwait(false);
            try
            {
                int position = 0;
                do { int count = Math.Min(fragmentBytes, bytes.Length - position); await Socket.SendAsync(bytes.AsMemory(position, count), type, position + count == bytes.Length, token).ConfigureAwait(false); position += count; }
                while (position < bytes.Length);
            }
            finally { _write.Release(); }
        }
        /// <summary>关闭回应与普通发送共享单写入锁。</summary>
        public async Task CloseAsync(CancellationToken token)
        { await _write.WaitAsync(token).ConfigureAwait(false); try { await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", token).ConfigureAwait(false); } finally { _write.Release(); } }
    }
}

/// <summary>Mock 收到的真实 API 请求及其所属连接的响应入口。</summary>
public sealed class MockAction
{
    private readonly Func<string, CancellationToken, Task> _respond;
    public JsonElement Request { get; }
    public string Action => Request.GetProperty("action").GetString()!;
    public JsonElement Parameters => Request.GetProperty("params");
    /// <summary>保留所属连接，乱序响应仍返回最初的 echo。</summary>
    public MockAction(JsonElement request, Func<string, CancellationToken, Task> respond) { Request = request; _respond = respond; }
    /// <summary>独立构造标准回执，测试可控制成功、失败或异步受理。</summary>
    public Task RespondAsync(string status, int retcode, JsonElement? data, CancellationToken token = default) => _respond(JsonSerializer.Serialize(new Dictionary<string, object?>
    { ["status"] = status, ["retcode"] = retcode, ["data"] = data, ["echo"] = Request.GetProperty("echo") }), token);
    /// <summary>发送精确异常响应，供缺字段和未知 echo 场景使用。</summary>
    public Task RespondRawAsync(string json, CancellationToken token = default) => _respond(json, token);
}
