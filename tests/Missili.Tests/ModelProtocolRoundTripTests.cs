using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>三种协议的工具和原生续接通过实际 SDK 往返，不执行真实工具。</summary>
public sealed class ModelProtocolRoundTripTests
{
    /// <summary>保留完整参数、推理项和签名，再显式提交合成工具结果。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, false)]
    [InlineData(ModelProtocol.ChatCompletions, true)]
    [InlineData(ModelProtocol.Responses, false)]
    [InlineData(ModelProtocol.Responses, true)]
    [InlineData(ModelProtocol.Anthropic, false)]
    [InlineData(ModelProtocol.Anthropic, true)]
    public async Task ToolResultsAndNativeStateRoundTrip(ModelProtocol protocol, bool streaming)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument native = JsonDocument.Parse(NativeResponse(protocol));
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps =
            [new ModelMockStep { Response = native.RootElement.Clone(), Events = Events(protocol, native.RootElement) }, new ModelMockStep { Text = "已取得工具结果" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_NATIVE_TEST_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ConversationConfigurationTests.Configuration(protocol == ModelProtocol.Anthropic ? mock.BaseAddress : new Uri(mock.BaseAddress, "/v1"), variable);
            config = config with { Providers = [config.Providers[0] with { Protocol = protocol }], Models = [config.Models[0] with { Tools = true }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ConversationProfileSnapshot profile = fixture.Runtime.Profiles.Resolve(TestStore.Address());
            using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"],"additionalProperties":false}""");
            ModelRequest request = new()
            {
                Purpose = "protocol.test",
                Profile = profile,
                Candidates = ["A"],
                Stream = streaming,
                Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("合成工具往返")] }]),
                Tools = [new ModelTool { Name = "synthetic_tool", Description = "仅声明，不执行", Parameters = schema.RootElement }]
            };
            ModelResult first = await fixture.Runtime.Models.GenerateAsync(request, cancellationToken: token);
            Assert.Equal(ModelCallState.Succeeded, first.State); Assert.True(first.Complete, first.Error);
            ModelBlock tool = Assert.Single(first.Content, item => item.Kind == ModelBlockKind.ToolCall);
            Assert.True(tool.Complete); Assert.Equal(42, tool.Data!.Value.GetProperty("value").GetInt32());
            Assert.Equal(protocol != ModelProtocol.ChatCompletions, first.Continuation is not null);
            ModelResult second = await fixture.Runtime.Models.GenerateAsync(request with
            {
                Id = Guid.NewGuid(),
                Input = request.Input with
                {
                    Items = request.Input.Items.AddRange(new ModelInputItem[] {
                    new ModelTurnInput { Message = first.ToAssistantMessage() },
                    new ModelTurnInput { Message = new ModelMessage { Role = "tool", Content = [new ModelBlock { Kind = ModelBlockKind.ToolResult, ToolCallId = tool.ToolCallId, Text = "synthetic-result" }] } } })
                }
            }, cancellationToken: token);
            Assert.Equal(ModelCallState.Succeeded, second.State); Assert.Equal("已取得工具结果", second.Text);
            JsonElement body = mock.Requests[1].Body!.Value;
            if (protocol == ModelProtocol.Responses)
            {
                JsonElement inputs = body.GetProperty("input");
                Assert.Equal("opaque-reasoning", inputs[1].GetProperty("encrypted_content").GetString());
                Assert.Equal("function_call", inputs[2].GetProperty("type").GetString());
                Assert.Equal("function_call_output", inputs[3].GetProperty("type").GetString());
                Assert.Equal("call-one", inputs[3].GetProperty("call_id").GetString());
            }
            else if (protocol == ModelProtocol.Anthropic)
            {
                JsonElement messages = body.GetProperty("messages");
                Assert.Equal("synthetic-signature", messages[1].GetProperty("content")[0].GetProperty("signature").GetString());
                Assert.Equal("call-one", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
            }
            else Assert.Equal("call-one", body.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
            Assert.Equal(2, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>依据各协议直接定义完整结构，推理内容仅为合成的不透明测试数据。</summary>
    internal static string NativeResponse(ModelProtocol protocol) => protocol switch
    {
        ModelProtocol.ChatCompletions => """{"id":"chat-one","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","content":"已准备工具","tool_calls":[{"id":"call-one","type":"function","function":{"name":"synthetic_tool","arguments":"{\"value\":42}"}}]},"finish_reason":"tool_calls"}]}""",
        ModelProtocol.Responses => """{"id":"resp-one","object":"response","created_at":1,"status":"completed","model":"A","output":[{"type":"reasoning","id":"reason-one","summary":[],"encrypted_content":"opaque-reasoning"},{"type":"function_call","id":"function-one","call_id":"call-one","name":"synthetic_tool","arguments":"{\"value\":42}","status":"completed"}],"parallel_tool_calls":true,"tools":[],"tool_choice":"auto","store":false,"temperature":1,"top_p":1,"text":{"format":{"type":"text"}},"metadata":{}}""",
        ModelProtocol.Anthropic => """{"id":"msg-one","type":"message","role":"assistant","model":"A","content":[{"type":"thinking","thinking":"合成原生内容","signature":"synthetic-signature"},{"type":"tool_use","id":"call-one","name":"synthetic_tool","input":{"value":42}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":2}}""",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    /// <summary>流式工具参数由两个片段组成，终止事件必须实际到达才能得到完整调用。</summary>
    internal static ImmutableArray<string> Events(ModelProtocol protocol, JsonElement response)
    {
        if (protocol == ModelProtocol.Responses)
            return ["event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":" + response.GetRawText() + "}\n\n"];
        if (protocol == ModelProtocol.ChatCompletions)
            return ["data: {\"id\":\"chat-one\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"A\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"已准备工具\",\"tool_calls\":[{\"index\":0,\"id\":\"call-one\",\"type\":\"function\",\"function\":{\"name\":\"synthetic_tool\",\"arguments\":\"{\\\"value\\\":\"}}]},\"finish_reason\":null}]}\n\n",
                "data: {\"id\":\"chat-one\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"A\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"42}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n", "data: [DONE]\n\n"];
        return [
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-one\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"A\",\"content\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"合成原生内容\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"synthetic-signature\"}}\n\n",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call-one\",\"name\":\"synthetic_tool\",\"input\":{}}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"value\\\":\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"42}\"}}\n\n",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":2}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
        ];
    }
}
