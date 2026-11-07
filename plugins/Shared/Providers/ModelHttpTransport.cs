using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;

namespace Missili.Providers;

/// <summary>编译进各插件的请求传输层，负责期限和原始诊断，不替代 SDK 协议解析。</summary>
internal sealed class ModelHttpTransport : DelegatingHandler
{
    private readonly ModelAttemptRequest _attempt;
    private readonly IModelAttemptObserver _observer;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _mediaValues;

    /// <summary>每个实际调用独占处理器和流，不把插件对象交给宿主全局 HTTP 工厂。</summary>
    public ModelHttpTransport(ModelAttemptRequest attempt, IModelAttemptObserver observer, TimeProvider clock, IReadOnlyDictionary<string, string> mediaValues)
    {
        _attempt = attempt; _observer = observer; _clock = clock; _mediaValues = mediaValues;
        InnerHandler = new SocketsHttpHandler
        {
            ConnectTimeout = attempt.Request.Profile.Profile.Execution.ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.All,
            // 取消的长流不能在返回后继续后台排空；每次尝试已经独占客户端，无需为复用连接排空。
            // https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.maxresponsedrainsize
            MaxResponseDrainSize = 0,
            AllowAutoRedirect = false
        };
    }

    /// <summary>在实际网络调用前检查最终编码体积并保存脱离凭据的请求正文。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && request.Content.Headers.ContentType?.MediaType == "application/json")
        {
            byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.LongLength > _attempt.Model.MaxRequestBytes) throw new ModelInputException("编码后的模型请求超过载荷上限。");
            JsonNode node = JsonNode.Parse(body) ?? throw new JsonException("请求 JSON 为空。");
            Sanitize(node);
            await _observer.RequestAsync(JsonSerializer.SerializeToElement(node), cancellationToken).ConfigureAwait(false);
        }
        using CancellationTokenSource deadline = new(_attempt.Request.Profile.Profile.Execution.HeadersTimeout, _clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        HttpResponseMessage response;
        try { response = await base.SendAsync(request, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("等待模型响应头超时。"); }
        _observer.ResponseHeaders((int)response.StatusCode,
            response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() :
            response.Headers.TryGetValues("request-id", out ids) ? ids.FirstOrDefault() : null);
        HttpContent original = response.Content;
        Stream stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        StreamContent wrapped = new(new ObservedStream(stream, original, _observer,
            _attempt.Request.Profile.Profile.Execution.ReadIdleTimeout, _clock));
        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            wrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = wrapped;
        if (_attempt.Provider.Protocol == ModelProtocol.Anthropic && !response.IsSuccessStatusCode)
        {
            // SDK 错误工厂会用共享 JSON 选项反序列化私有 ErrorType，导致加载上下文无法回收。
            // 在传输边界完整记录错误正文并抛出 BCL 异常，仍由 SDK 执行请求且不改变重试次数。
            // https://github.com/anthropics/anthropic-sdk-csharp/blob/6eae95e4689477382c81bb5b35b6386ff5b3686d/src/Anthropic/Exceptions/AnthropicExceptionFactory.cs
            try
            {
                await wrapped.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException("模型服务返回 HTTP 错误。", null, response.StatusCode);
            }
            finally { response.Dispose(); }
        }
        return response;
    }

    /// <summary>将实际请求中的二进制替换为可定位原件引用，文本及其他参数保持原样。</summary>
    private void Sanitize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> item in obj.ToArray())
            {
                if (item.Value is JsonValue value && value.TryGetValue(out string? text) && _mediaValues.TryGetValue(text, out string? reference))
                    obj[item.Key] = reference;
                else if (item.Value is not null) Sanitize(item.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array) if (child is not null) Sanitize(child);
        }
    }

    /// <summary>每次真实读取单独计时，协议心跳也会重置读取空闲期限。</summary>
    private sealed class ObservedStream : Stream
    {
        private readonly Stream _source;
        private readonly HttpContent _content;
        private readonly IModelAttemptObserver _observer;
        private readonly TimeSpan _idle;
        private readonly TimeProvider _clock;
        /// <summary>同时持有原始响应资源，外层释放时一并结束。</summary>
        public ObservedStream(Stream source, HttpContent content, IModelAttemptObserver observer, TimeSpan idle, TimeProvider clock)
        { _source = source; _content = content; _observer = observer; _idle = idle; _clock = clock; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <summary>记录 SDK 实际读取的原始字节，截断流仍保留已有内容。</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource deadline = new(_idle, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            int count;
            try { count = await _source.ReadAsync(buffer, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("模型响应连续无数据超时。"); }
            if (count > 0) await _observer.ResponseAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            return count;
        }
        /// <summary>兼容 SDK 的数组读取入口，仍经过相同期限和诊断路径。</summary>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        /// <summary>异步客户端不允许通过同步读取绕过期限。</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("模型响应必须异步读取。");
        /// <summary>释放原始响应；其内容负责释放底层流。</summary>
        protected override void Dispose(bool disposing) { if (disposing) _content.Dispose(); base.Dispose(disposing); }
        /// <summary>只读响应不支持刷新。</summary>
        public override void Flush() => throw new NotSupportedException();
        /// <summary>网络流不支持定位。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>只读响应不支持修改长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <summary>只读响应不支持写入。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
