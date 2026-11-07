using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Missili.Model.Mock;

/// <summary>按请求顺序配置合成响应；测试夹具不依赖任何生产协议编码器。</summary>
public sealed record ModelMockScenario
{
    public ImmutableArray<ModelMockStep> Steps { get; init; } = [];
    public ModelMockStep Default { get; init; } = new();
    public bool StrictSteps { get; init; }
}

/// <summary>一个请求的成功、失败、暂停、分片和截断行为。</summary>
public sealed record ModelMockStep
{
    public string? Model { get; init; }
    public string? Path { get; init; }
    public int Status { get; init; } = 200;
    public string Text { get; init; } = "模型 Mock 的合成回复。";
    public string? WaitForSignal { get; init; }
    public int FragmentBytes { get; init; }
    public int DelayMilliseconds { get; init; }
    public bool Truncated { get; init; }
    public ImmutableArray<string> TextParts { get; init; } = [];
    public ImmutableArray<string> Events { get; init; } = [];
    public ImmutableDictionary<int, string> BeforeEventSignals { get; init; } = ImmutableDictionary<int, string>.Empty;
    public JsonElement? Response { get; init; }
}

/// <summary>只记录测试所需请求数据；不记录鉴权头或音频二进制。</summary>
public sealed record ModelMockRequest
{
    public required int Sequence { get; init; }
    public required string Path { get; init; }
    public required string Model { get; init; }
    public bool Streaming { get; init; }
    public JsonElement? Body { get; init; }
    public long BodyBytes { get; init; }
    public string? AudioFileName { get; init; }
    public string? AudioMimeType { get; init; }
    public long? AudioBytes { get; init; }
}

/// <summary>真实 HTTP/SSE 对端，可独立启动或被集成测试控制。</summary>
public sealed class ModelMockServer : IAsyncDisposable
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
    private readonly WebApplication _application;
    private readonly ModelMockScenario _scenario;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);
    private readonly Channel<ModelMockRequest> _requests = Channel.CreateBounded<ModelMockRequest>(256);
    private readonly ConcurrentQueue<ModelMockRequest> _history = new();
    private int _count;
    private int _disposed;
    public Uri BaseAddress { get; private set; } = null!;
    public int TotalRequests => Volatile.Read(ref _count);
    public ImmutableArray<ModelMockRequest> Requests => _history.ToImmutableArray();

    /// <summary>保存真实应用与合成场景，所有响应只由场景内容决定。</summary>
    private ModelMockServer(WebApplication application, ModelMockScenario scenario, TimeProvider clock)
    { _application = application; _scenario = scenario; _clock = clock; }

    /// <summary>启动有限请求体的 HTTP 服务，返回实际绑定地址。</summary>
    public static async Task<ModelMockServer> StartAsync(ModelMockScenario scenario, string url, CancellationToken token, TimeProvider? clock = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
        WebApplication app = builder.Build();
        ModelMockServer server = new(app, scenario, clock ?? TimeProvider.System);
        app.Run(server.HandleAsync);
        await app.StartAsync(token).ConfigureAwait(false);
        server.BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return server;
    }

    /// <summary>释放指定信号，允许测试控制时序而不依靠固定等待猜测。</summary>
    public void Release(string signal) => _signals.GetOrAdd(signal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    /// <summary>等待下一条实际网络请求；通道有界，未消费时对端施加背压。</summary>
    public Task<ModelMockRequest> NextRequestAsync(CancellationToken token) => _requests.Reader.ReadAsync(token).AsTask();

    /// <summary>解析标准请求后选择场景响应，模型或路径不符时明确返回失败。</summary>
    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Path == "/health/live") { await context.Response.WriteAsync("ready", context.RequestAborted); return; }
        string path = context.Request.Path.Value!;
        if (context.Request.Method != "POST" || path is not ("/v1/chat/completions" or "/v1/responses" or "/v1/messages" or "/v1/audio/transcriptions"))
        { context.Response.StatusCode = 404; return; }
        CancellationToken token = context.RequestAborted;
        JsonElement? body = null;
        string model;
        bool stream;
        string? fileName = null;
        string? mimeType = null;
        long? audioLength = null;
        long bodyBytes = context.Request.ContentLength ?? 0;
        if (path == "/v1/audio/transcriptions")
        {
            IFormCollection form = await context.Request.ReadFormAsync(token).ConfigureAwait(false);
            model = form["model"].ToString(); stream = form["stream"].ToString() == "true";
            IFormFile? file = form.Files.GetFile("file"); fileName = file?.FileName; mimeType = file?.ContentType; audioLength = file?.Length;
            if (file is null) { context.Response.StatusCode = 400; return; }
        }
        else
        {
            using MemoryStream received = new();
            await context.Request.Body.CopyToAsync(received, token).ConfigureAwait(false); bodyBytes = received.Length; received.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(received, cancellationToken: token).ConfigureAwait(false);
            body = document.RootElement.Clone(); model = body.Value.GetProperty("model").GetString()!;
            stream = body.Value.TryGetProperty("stream", out JsonElement requested) && requested.ValueKind == JsonValueKind.True;
        }
        int sequence = Interlocked.Increment(ref _count);
        ModelMockRequest request = new()
        {
            Sequence = sequence,
            Path = path,
            Model = model,
            Streaming = stream,
            Body = body,
            BodyBytes = bodyBytes,
            AudioFileName = fileName,
            AudioMimeType = mimeType,
            AudioBytes = audioLength
        };
        _history.Enqueue(request);
        while (_history.Count > 256) _history.TryDequeue(out ModelMockRequest? discarded);
        await _requests.Writer.WriteAsync(request, token).ConfigureAwait(false);
        ModelMockStep step = sequence <= _scenario.Steps.Length ? _scenario.Steps[sequence - 1] : _scenario.Default;
        if (_scenario.StrictSteps && sequence > _scenario.Steps.Length || step.Model is not null && step.Model != model || step.Path is not null && step.Path != path)
        { context.Response.StatusCode = 409; await context.Response.WriteAsync("{\"error\":{\"message\":\"unexpected mock request\"}}", token); return; }
        if (step.WaitForSignal is string signal) await _signals.GetOrAdd(signal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token).ConfigureAwait(false);
        context.Response.StatusCode = step.Status;
        if (step.Status >= 400)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(step.Response?.GetRawText() ?? "{\"error\":{\"message\":\"synthetic model failure\",\"type\":\"server_error\"}}", token); return;
        }
        if (!stream)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(step.Response?.GetRawText() ?? Response(path, model, step.Text).ToJsonString(), token); return;
        }
        context.Response.ContentType = "text/event-stream";
        IEnumerable<string> events = step.Events.IsEmpty ? Events(path, model, step) : step.Events;
        int eventIndex = 0;
        foreach (string value in events)
        {
            if (step.BeforeEventSignals.TryGetValue(eventIndex++, out string? eventSignal))
                await _signals.GetOrAdd(eventSignal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            int size = step.FragmentBytes > 0 ? step.FragmentBytes : bytes.Length;
            for (int offset = 0; offset < bytes.Length; offset += size)
            {
                await context.Response.Body.WriteAsync(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)), token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
            if (step.DelayMilliseconds > 0) await Task.Delay(TimeSpan.FromMilliseconds(step.DelayMilliseconds), _clock, token).ConfigureAwait(false);
        }
    }

    /// <summary>独立标准夹具，完整响应包含客户端必须读取的结束状态。</summary>
    private static JsonObject Response(string path, string model, string text)
    {
        if (path == "/v1/audio/transcriptions") return new JsonObject { ["text"] = text };
        if (path == "/v1/chat/completions") return new JsonObject
        {
            ["id"] = "chat-synthetic",
            ["object"] = "chat.completion",
            ["created"] = 1,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "stop", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 3, ["completion_tokens"] = 2, ["total_tokens"] = 5 }
        };
        if (path == "/v1/messages") return new JsonObject
        {
            ["id"] = "msg-synthetic",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = "end_turn",
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = 2 }
        };
        return new JsonObject
        {
            ["id"] = "resp-synthetic",
            ["object"] = "response",
            ["created_at"] = 1,
            ["status"] = "completed",
            ["model"] = model,
            ["error"] = null,
            ["incomplete_details"] = null,
            ["instructions"] = null,
            ["max_output_tokens"] = 4096,
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["id"] = "msg-synthetic",
                ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() })
            }),
            ["parallel_tool_calls"] = true,
            ["previous_response_id"] = null,
            ["store"] = false,
            ["temperature"] = 1,
            ["top_p"] = 1,
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "text" } },
            ["reasoning"] = new JsonObject { ["effort"] = null, ["summary"] = null },
            ["tools"] = new JsonArray(),
            ["tool_choice"] = "auto",
            ["truncation"] = "disabled",
            ["metadata"] = new JsonObject(),
            ["usage"] = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = 2, ["total_tokens"] = 5 }
        };
    }

    /// <summary>按四种协议独立产生事件，不借用被测提供商的编码器。</summary>
    private static IEnumerable<string> Events(string path, string model, ModelMockStep step)
    {
        ImmutableArray<string> parts = step.TextParts.IsEmpty ? [step.Text] : step.TextParts;
        int sequence = 0;
        if (path == "/v1/messages")
        {
            JsonObject start = Response(path, model, ""); start["content"] = new JsonArray(); start["stop_reason"] = null;
            yield return Event("message_start", new JsonObject { ["type"] = "message_start", ["message"] = start });
            yield return Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
        }
        foreach (string part in parts)
        {
            if (path == "/v1/chat/completions") yield return Event(null, new JsonObject
            {
                ["id"] = "chat-synthetic",
                ["object"] = "chat.completion.chunk",
                ["created"] = 1,
                ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = part }, ["finish_reason"] = null })
            });
            else if (path == "/v1/responses") yield return Event("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["item_id"] = "msg-synthetic", ["output_index"] = 0, ["content_index"] = 0, ["sequence_number"] = sequence++, ["delta"] = part });
            else if (path == "/v1/messages") yield return Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = part } });
            else yield return Event(null, new JsonObject { ["type"] = "transcript.text.delta", ["delta"] = part });
        }
        if (step.Truncated) yield break;
        string text = string.Concat(parts);
        if (path == "/v1/chat/completions")
        {
            yield return Event(null, new JsonObject
            {
                ["id"] = "chat-synthetic",
                ["object"] = "chat.completion.chunk",
                ["created"] = 1,
                ["model"] = model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = "stop" })
            });
            yield return "data: [DONE]\n\n";
        }
        else if (path == "/v1/responses") yield return Event("response.completed", new JsonObject { ["type"] = "response.completed", ["sequence_number"] = sequence, ["response"] = Response(path, model, text) });
        else if (path == "/v1/messages")
        {
            yield return Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
            yield return Event("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["output_tokens"] = 2 } });
            yield return Event("message_stop", new JsonObject { ["type"] = "message_stop" });
        }
        else yield return Event(null, new JsonObject { ["type"] = "transcript.text.done", ["text"] = text });
    }

    /// <summary>形成标准 SSE 记录；字节分片由实际 HTTP 写入路径执行。</summary>
    private static string Event(string? name, JsonObject data) => (name is null ? "" : "event: " + name + "\n") + "data: " + data.ToJsonString() + "\n\n";

    /// <summary>结束本 Mock 的连接和控制等待，不触碰任何真实模型服务。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (TaskCompletionSource signal in _signals.Values) signal.TrySetCanceled();
        _requests.Writer.TryComplete();
        await _application.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
