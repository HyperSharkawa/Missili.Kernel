using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>候选切换依据必要原生状态，而不是根据是否曾使用工具锁死模型。</summary>
public sealed class ModelContinuationTests
{
    /// <summary>已经完成的标准工具组能够跨协议编码，失败时只重试模型请求。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, ModelProtocol.Responses)]
    [InlineData(ModelProtocol.ChatCompletions, ModelProtocol.Anthropic)]
    [InlineData(ModelProtocol.Responses, ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses, ModelProtocol.Anthropic)]
    [InlineData(ModelProtocol.Anthropic, ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Anthropic, ModelProtocol.Responses)]
    public async Task PortableToolTurnsCanSwitchProtocol(ModelProtocol firstProtocol, ModelProtocol fallbackProtocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JsonObject response = JsonNode.Parse(ModelProtocolRoundTripTests.NativeResponse(firstProtocol))!.AsObject();
        if (firstProtocol == ModelProtocol.Responses) response["output"]!.AsArray().RemoveAt(0);
        if (firstProtocol == ModelProtocol.Anthropic) response["content"]!.AsArray().RemoveAt(0);
        using JsonDocument document = JsonDocument.Parse(response.ToJsonString());
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps = [new ModelMockStep { Response = document.RootElement.Clone() }, new ModelMockStep { Status = 500 }, new ModelMockStep { Text = "跨协议续接成功" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_CONTINUATION_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, firstProtocol);
            configuration = configuration with
            {
                Providers = [configuration.Providers[0], configuration.Providers[0] with
                { Id = "fallback", Protocol = fallbackProtocol, BaseUrl = fallbackProtocol == ModelProtocol.Anthropic ? mock.BaseAddress : new Uri(mock.BaseAddress, "/v1") }],
                Models = [configuration.Models[0] with { Tools = true }, configuration.Models[1] with { Tools = true, ProviderId = "fallback" }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(fixture, firstProtocol, token);
            if ((firstProtocol == ModelProtocol.Anthropic) != (fallbackProtocol == ModelProtocol.Anthropic))
                await ModelMediaTests.LoadProviderAsync(fixture, fallbackProtocol, token);
            ModelRequest request = ModelFailureTests.Request(fixture) with { Candidates = ["A"], Stream = false };
            ModelResult first = await fixture.Runtime.Models.GenerateAsync(request, cancellationToken: token);
            Assert.True(first.Complete, first.Error);
            Assert.Null(first.Continuation);
            ModelBlock tool = Assert.Single(first.Content, block => block.Kind == ModelBlockKind.ToolCall);
            ModelResult second = await fixture.Runtime.Models.GenerateAsync(request with
            {
                Id = Guid.NewGuid(),
                Candidates = ["A", "B"],
                Input = request.Input with
                {
                    Items = [.. request.Input.Items,
                    new ModelTurnInput { Message = first.ToAssistantMessage() },
                    new ModelTurnInput { Message = new ModelMessage { Role = "tool", Content =
                        [new ModelBlock { Kind = ModelBlockKind.ToolResult, ToolCallId = tool.ToolCallId, Text = "已经完成的唯一工具结果" }] } }]
                }
            }, cancellationToken: token);
            Assert.True(second.Complete, second.Error);
            Assert.Equal("B", second.ModelId);
            Assert.Equal(500, second.Attempts[0].HttpStatus);
            Assert.NotNull(second.Attempts[0].DetailId);
            Assert.Empty(second.ExcludedCandidates);
            Assert.Equal(new[] { "A", "A", "B" }, mock.Requests.Select(item => item.Model));
            JsonElement fallback = mock.Requests[2].Body!.Value;
            Assert.Contains("call-one", fallback.ToString());
            Assert.Contains("synthetic_tool", fallback.ToString());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>带签名或推理状态的轮次跳过不合格候选，跳过行为不增加实际请求次数。</summary>
    [Theory]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task NativeHistoryRestrictsCandidatesWithoutRejectingEligibleOne(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol));
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps = [new ModelMockStep { Response = response.RootElement.Clone() }, new ModelMockStep { Text = "保持原生来源" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_NATIVE_BOUND_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray() };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelRequest request = ModelFailureTests.Request(fixture) with { Candidates = ["A"], Stream = false };
            ModelResult first = await fixture.Runtime.Models.GenerateAsync(request, cancellationToken: token);
            Assert.NotNull(first.Continuation);
            ModelResult second = await fixture.Runtime.Models.GenerateAsync(request with
            {
                Id = Guid.NewGuid(),
                Candidates = ["B", "A"],
                Input = request.Input with
                {
                    Items = [.. request.Input.Items,
                    new ModelTurnInput { Message = first.ToAssistantMessage() },
                    new ModelTurnInput { Message = new ModelMessage { Role = "tool", Content =
                        [new ModelBlock { Kind = ModelBlockKind.ToolResult, ToolCallId = "call-one", Text = "已有结果" }] } }]
                }
            }, cancellationToken: token);
            Assert.True(second.Complete, second.Error);
            Assert.Equal("B", Assert.Single(second.ExcludedCandidates).ModelId);
            ModelCallRecord saved = (await fixture.Runtime.Models.Calls.GetCallAsync(second.CallId, token))!;
            Assert.Equal("B", Assert.Single(saved.ExcludedCandidates).ModelId);
            Assert.Single(second.Attempts);
            Assert.Equal(new[] { "A", "A" }, mock.Requests.Select(item => item.Model));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}
