using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Missili.Abstractions;
using Missili.Nexus;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实 DLL、真实 SDK 和回环 HTTP 验证协议及实际卸载。</summary>
public sealed class ModelProviderTests
{
    /// <summary>流式和非流式都经过 SDK；测试不会静态加载待卸载的提供商程序集。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, false)]
    [InlineData(ModelProtocol.ChatCompletions, true)]
    [InlineData(ModelProtocol.Responses, false)]
    [InlineData(ModelProtocol.Responses, true)]
    [InlineData(ModelProtocol.Anthropic, false)]
    [InlineData(ModelProtocol.Anthropic, true)]
    public async Task RealSdkReturnsTextAndUnloads(ModelProtocol protocol, bool streaming)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: token);
            Assert.Equal("synthetic-model", body.RootElement.GetProperty("model").GetString());
            context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            await context.Response.WriteAsync(Response(protocol, streaming), token);
        }, token);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = protocol == ModelProtocol.Anthropic ? "anthropic" : "openai";
        PluginStatus loaded = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Providers", package), RuntimeFixture.Configuration(), token);
        Assert.Equal(PluginState.Active, loaded.State);
        string credentialVariable = "MISSILI_SYNTHETIC_MODEL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credentialVariable, "synthetic-local-only");
        try
        {
            ModelProviderDefinition provider = new() { Id = "test", Protocol = protocol, BaseUrl = server.BaseAddress, ApiKeyEnvironmentVariable = credentialVariable };
            ModelDefinition model = new() { Id = "test", ProviderId = "test", Name = "synthetic-model" };
            ConversationProfileSnapshot profile = new() { Revision = 1, Profile = new ConversationProfile { Id = "test", ReplyModels = ["test"] }, Models = ImmutableDictionary<string, ModelDefinition>.Empty.Add("test", model), Providers = ImmutableDictionary<string, ModelProviderDefinition>.Empty.Add("test", provider) };
            ModelAttemptRequest request = new()
            {
                AttemptId = Guid.NewGuid(),
                Model = model,
                Provider = provider,
                Request = new ModelRequest { Purpose = "test", Profile = profile, Candidates = ["test"], Stream = streaming, Messages = [new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("测试")] }] }
            };
            ModelProtocolResult result = await InvokeAsync(fixture.Runtime.ModelProviders, request, token);
            Assert.True(result.Complete, result.Error);
            Assert.Equal(ModelCallState.Succeeded, result.State);
            Assert.Equal("你好", string.Concat(result.Content.Where(block => block.Kind == ModelBlockKind.Text).Select(block => block.Text)));
            Assert.Equal(1, calls);
            PluginStatus stopped = await fixture.Runtime.Plugins.StopAsync(loaded.Id, token);
            Assert.Equal(PluginState.Stopped, stopped.State);
            Assert.Null(fixture.Runtime.ModelProviders.TryAcquire(protocol));
        }
        finally { Environment.SetEnvironmentVariable(credentialVariable, null); }
        // RuntimeFixture 最终检查弱引用失效，不能用仅调用 Unload 代替回收证据。
    }

    /// <summary>单独栈帧结束后不持有已释放的插件资格。</summary>
    private static async Task<ModelProtocolResult> InvokeAsync(IModelProviderRuntime providers, ModelAttemptRequest request, CancellationToken token)
    {
        using IModelProviderLease lease = providers.TryAcquire(request.Provider.Protocol)!;
        return await lease.ExecuteAsync(request, new Observer(), token);
    }

    /// <summary>夹具直接依照协议编写，不使用生产编码器构造预期响应。</summary>
    private static string Response(ModelProtocol protocol, bool stream) => (protocol, stream) switch
    {
        (ModelProtocol.ChatCompletions, false) => """{"id":"chat-1","object":"chat.completion","created":1,"model":"synthetic-model","choices":[{"index":0,"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
        (ModelProtocol.ChatCompletions, true) => "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"synthetic-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"你好\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"synthetic-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        (ModelProtocol.Responses, false) => ResponsesResult,
        (ModelProtocol.Responses, true) => "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg-1\",\"output_index\":0,\"content_index\":0,\"sequence_number\":1,\"delta\":\"你好\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":" + ResponsesResult + "}\n\n",
        (ModelProtocol.Anthropic, false) => """{"id":"msg-1","type":"message","role":"assistant","model":"synthetic-model","content":[{"type":"text","text":"你好"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":2}}""",
        (ModelProtocol.Anthropic, true) => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"synthetic-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private const string ResponsesResult = """{"id":"resp-1","object":"response","created_at":1,"status":"completed","error":null,"incomplete_details":null,"instructions":null,"max_output_tokens":4096,"model":"synthetic-model","output":[{"type":"message","id":"msg-1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"你好","annotations":[]}]}],"parallel_tool_calls":true,"previous_response_id":null,"reasoning":{"effort":null,"summary":null},"store":false,"temperature":1,"text":{"format":{"type":"text"}},"tool_choice":"auto","tools":[],"top_p":1,"truncation":"disabled","usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5},"metadata":{}}""";

    /// <summary>此层测试只验证 SDK 和生命周期，调用记录由协调器测试验证。</summary>
    private sealed class Observer : IModelAttemptObserver
    {
        /// <summary>消费真实请求，不将测试数据写入长期存储。</summary>
        public Task RequestAsync(JsonElement request, CancellationToken cancellationToken) => Task.CompletedTask;
        /// <summary>消费真实响应字节，避免测试额外持有插件对象。</summary>
        public Task ResponseAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => Task.CompletedTask;
        /// <summary>确认已收到实际 HTTP 响应状态。</summary>
        public void ResponseHeaders(int status, string? requestId) => Assert.Equal(200, status);
        /// <summary>增量使用共享数据边界，不接收 SDK 类型。</summary>
        public Task UpdateAsync(string kind, string? text, JsonElement? data, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
