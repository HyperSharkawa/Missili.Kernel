using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Tools.ModelMock;
using Xunit;

namespace Missili.Tests;

/// <summary>工具参数与原生块的流式完整性独立于最终纯文本结果。</summary>
public sealed class ModelStreamTests
{
    /// <summary>中断工具流保留实际片段，不把初始空对象当作完整工具参数。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task TruncatedToolStreamRemainsReadableButNotExecutable(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument native = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol));
        ImmutableArray<string> events = protocol == ModelProtocol.Responses
            ? ["event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"item-one\",\"call_id\":\"call-one\",\"name\":\"synthetic_tool\",\"arguments\":\"\",\"status\":\"in_progress\"}}\n\n",
               "event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":2,\"output_index\":0,\"item_id\":\"item-one\",\"delta\":\"{\\\"value\\\":\"}\n\n"]
            : ModelProtocolRoundTripTests.Events(protocol, native.RootElement).Take(protocol == ModelProtocol.Anthropic ? 7 : 1).ToImmutableArray();
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { Events = events, FragmentBytes = 1 } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_STREAM_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol));
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, cancellationToken: token);
            Assert.Equal(ModelCallState.Failed, result.State); Assert.False(result.Complete); Assert.Null(result.Continuation);
            ModelBlock tool = Assert.Single(result.Content, block => block.Kind == ModelBlockKind.ToolCall);
            Assert.False(tool.Complete); Assert.Null(tool.Data); Assert.Contains("value", tool.Text);
            if (protocol == ModelProtocol.Anthropic)
                Assert.Contains(result.Content, block => block.Kind == ModelBlockKind.Native && block.Data!.Value.GetProperty("signature").GetString() == "synthetic-signature");
            Assert.Equal(1, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>多行 SSE、注释心跳和 UTF-8 分片必须还原为同一个真实协议事件。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task MultilineEventsAndHeartbeatsKeepNativeState(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument native = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol));
        ImmutableArray<string> events = ModelProtocolRoundTripTests.Events(protocol, native.RootElement)
            .Select(value => ": synthetic heartbeat\n\n" + value.Replace("\"type\":", "\ndata: \"type\":", StringComparison.Ordinal)).ToImmutableArray();
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { Events = events, FragmentBytes = 2 } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_STREAM_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol));
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, cancellationToken: token);
            Assert.True(result.Complete, result.Error); Assert.Equal(1, mock.TotalRequests);
            Assert.Equal(42, Assert.Single(result.Content, block => block.Kind == ModelBlockKind.ToolCall).Data!.Value.GetProperty("value").GetInt32());
            Assert.DoesNotContain("heartbeat", result.Text);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}
