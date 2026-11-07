using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace Missili.Adapters.OneBot;

/// <summary>一条有大小上限的完整文本帧；超过阈值后只保留本任务暂存文件。</summary>
internal sealed class WirePayload : IAsyncDisposable
{
    private Stream _stream;
    private string? _path;
    public long Length => _stream.Length;
    /// <summary>为当前接收创建独立缓冲，不跨连接复用流位置。</summary>
    private WirePayload() => _stream = new MemoryStream();

    /// <summary>累计分片直到消息结束；载荷超限或二进制输入明确失败。</summary>
    public static async Task<WirePayload?> ReceiveAsync(WebSocket socket, OneBotOptions options, string temporaryDirectory, CancellationToken token)
    {
        WirePayload payload = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) { await payload.DisposeAsync().ConfigureAwait(false); return null; }
                if (received.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("OneBot 只接受文本 WebSocket 消息。");
                if (payload.Length + received.Count > options.MaxMessageBytes) throw new InvalidDataException("WebSocket JSON 载荷超过配置上限。");
                if (payload._path is null && payload.Length + received.Count > options.MemoryThreshold)
                {
                    string path = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".frame");
                    FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
                    payload._path = path;
                    try { payload._stream.Position = 0; await payload._stream.CopyToAsync(file, token).ConfigureAwait(false); }
                    catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
                    await payload._stream.DisposeAsync().ConfigureAwait(false); payload._stream = file;
                }
                await payload._stream.WriteAsync(buffer.AsMemory(0, received.Count), token).ConfigureAwait(false);
                if (received.EndOfMessage) return payload;
            }
        }
        catch { await payload.DisposeAsync().ConfigureAwait(false); throw; }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>完整分片结束后解析一次文档，调用者负责释放；UTF-8 边界由 JSON 读取器处理。</summary>
    public Task<JsonDocument> ParseAsync(int maxDepth, CancellationToken token)
    { _stream.Position = 0; return JsonDocument.ParseAsync(_stream, new JsonDocumentOptions { MaxDepth = maxDepth }, token); }

    /// <summary>只删除本对象创建的具体文件，不递归清理未知目录内容。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_path is not null) { File.Delete(_path); _path = null; }
    }
}

/// <summary>队列按累计字节计费，不能通过大量大 JSON 绕过条数限制。</summary>
internal sealed class ByteBudget
{
    private readonly object _gate = new();
    private readonly long _maximum;
    private long _used;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>建立单个适配器队列预算。</summary>
    public ByteBudget(long maximum) => _maximum = maximum;
    /// <summary>等待整个待入队载荷的容量，取消不扣留预算。</summary>
    public async Task AcquireAsync(long bytes, CancellationToken token)
    {
        if (bytes > _maximum) throw new InvalidDataException("单条载荷超过待处理预算。");
        while (true)
        {
            Task changed;
            lock (_gate)
            { if (_used + bytes <= _maximum) { _used += bytes; return; } changed = _changed.Task; }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }
    /// <summary>消费完成归还容量，并异步唤醒等待的有限连接。</summary>
    public void Release(long bytes)
    {
        lock (_gate)
        {
            _used -= bytes;
            TaskCompletionSource changed = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); changed.TrySetResult();
        }
    }
}
