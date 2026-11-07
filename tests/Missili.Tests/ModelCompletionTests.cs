using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 SDK 的终止原因必须约束候选策略，完整外形的工具不能掩盖截断。</summary>
public sealed class ModelCompletionTests
{
    /// <summary>输出限额切换候选，过滤和服务端暂停结束逻辑调用；两种传输方式遵守同一规则。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, "length", "OutputLimit", false, true)]
    [InlineData(ModelProtocol.ChatCompletions, "length", "OutputLimit", true, true)]
    [InlineData(ModelProtocol.Responses, "max_output_tokens", "OutputLimit", false, true)]
    [InlineData(ModelProtocol.Responses, "max_output_tokens", "OutputLimit", true, true)]
    [InlineData(ModelProtocol.Anthropic, "max_tokens", "OutputLimit", false, true)]
    [InlineData(ModelProtocol.Anthropic, "max_tokens", "OutputLimit", true, true)]
    [InlineData(ModelProtocol.ChatCompletions, "content_filter", "Filtered", false, false)]
    [InlineData(ModelProtocol.ChatCompletions, "content_filter", "Filtered", true, false)]
    [InlineData(ModelProtocol.Responses, "content_filter", "Filtered", false, false)]
    [InlineData(ModelProtocol.Responses, "content_filter", "Filtered", true, false)]
    [InlineData(ModelProtocol.Anthropic, "pause_turn", "UnsupportedStop", false, false)]
    [InlineData(ModelProtocol.Anthropic, "pause_turn", "UnsupportedStop", true, false)]
    public async Task IncompleteTerminationControlsFallback(ModelProtocol protocol, string reason, string errorKind, bool streaming, bool fallback)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ModelMockStep incomplete = IncompleteStep(protocol, reason);
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps = [incomplete, new ModelMockStep { Text = "完整的独立备用结果" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_COMPLETION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token,
                models: ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol));
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Stream = streaming }, cancellationToken: token);
            Assert.Equal(errorKind, result.Attempts[0].ErrorKind);
            Assert.True(result.Attempts[0].HasPartialResult);
            Assert.Equal(fallback ? 2 : 1, mock.TotalRequests);
            Assert.Equal(fallback, result.Complete);
            if (fallback)
            {
                Assert.Equal(ModelCallState.Succeeded, result.State);
                Assert.Equal("完整的独立备用结果", result.Text);
            }
            else
            {
                Assert.Equal(ModelCallState.Failed, result.State);
                Assert.Null(result.Continuation);
                Assert.All(result.Content, block => Assert.False(block.Complete));
                ModelBlock tool = Assert.Single(result.Content, block => block.Kind == ModelBlockKind.ToolCall);
                Assert.Null(tool.Data);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>标准夹具只改变终止原因，保留已结束的工具参数以暴露旧判断的错误。</summary>
    private static ModelMockStep IncompleteStep(ModelProtocol protocol, string reason)
    {
        JsonObject response = JsonNode.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol))!.AsObject();
        ImmutableArray<string> events;
        if (protocol == ModelProtocol.Responses)
        {
            response["status"] = "incomplete";
            response["incomplete_details"] = new JsonObject { ["reason"] = reason };
            events = ["event: response.incomplete\ndata: {\"type\":\"response.incomplete\",\"sequence_number\":1,\"response\":" + response.ToJsonString() + "}\n\n"];
        }
        else
        {
            if (protocol == ModelProtocol.ChatCompletions) response["choices"]![0]!["finish_reason"] = reason;
            else response["stop_reason"] = reason;
            using JsonDocument original = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol));
            string oldReason = protocol == ModelProtocol.ChatCompletions ? "\"finish_reason\":\"tool_calls\"" : "\"stop_reason\":\"tool_use\"";
            string newReason = protocol == ModelProtocol.ChatCompletions ? "\"finish_reason\":\"" + reason + "\"" : "\"stop_reason\":\"" + reason + "\"";
            events = ModelProtocolRoundTripTests.Events(protocol, original.RootElement)
                .Select(item => item.Replace(oldReason, newReason, StringComparison.Ordinal)).ToImmutableArray();
        }
        using JsonDocument document = JsonDocument.Parse(response.ToJsonString());
        return new ModelMockStep { Response = document.RootElement.Clone(), Events = events };
    }
}
