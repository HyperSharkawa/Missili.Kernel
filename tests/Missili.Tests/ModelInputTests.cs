using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实 SDK 验证资料切片与工具轮次的顺序，不能仅检查内存中的输入数组。</summary>
public sealed class ModelInputTests
{
    /// <summary>检查 SDK 实际发送的消息判别字段，覆盖工具前后拆分的 assistant 正文。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesMessagesKeepTheirWireType(bool streaming)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario(), "http://127.0.0.1:0", token);
        string variable = "MISSILI_RESPONSE_TYPE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.Responses);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray() };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.Responses, token);
            using JsonDocument arguments = JsonDocument.Parse("{}");
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(new ModelRequest
            {
                Purpose = "responses.message_type",
                Profile = fixture.Runtime.Profiles.Resolve(TestStore.Address()),
                Candidates = ["A"],
                Stream = streaming,
                Input = ModelInput.FromTurns([
                    new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("原始要求")] },
                    new ModelMessage { Role = "assistant", Content = [ModelBlock.TextBlock("调用前"),
                        new ModelBlock { Kind = ModelBlockKind.ToolCall, ToolCallId = "one", Name = "ordered_tool", Data = arguments.RootElement }, ModelBlock.TextBlock("调用后")] },
                    new ModelMessage { Role = "tool", Content = [new ModelBlock { Kind = ModelBlockKind.ToolResult, ToolCallId = "one", Text = "工具结果" }] },
                    new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("补充要求")] }
                ], "系统指令")
            }, cancellationToken: token);
            Assert.True(result.Complete, result.Error);
            JsonElement[] items = Assert.Single(mock.Requests).Body!.Value.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal(new[] { "message", "message", "message", "function_call", "message", "function_call_output", "message" },
                items.Select(item => item.GetProperty("type").GetString()));
            Assert.Equal(new[] { "system", "user", "assistant", "assistant", "user" },
                items.Where(item => item.GetProperty("type").GetString() == "message").Select(item => item.GetProperty("role").GetString()));
            Assert.Equal(new[] { "系统指令", "原始要求", "调用前", "调用后", "补充要求" },
                items.Where(item => item.GetProperty("type").GetString() == "message").Select(item => item.GetProperty("content")[0].GetProperty("text").GetString()));
            Assert.Equal("one", items[3].GetProperty("call_id").GetString());
            Assert.Equal("one", items[5].GetProperty("call_id").GetString());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>新聊天在工具结果之后追加，系统指令只出现一次，历史不覆盖已经完成的工具组。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task ConversationSlicesKeepTheirPositionAroundToolTurns(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario(), "http://127.0.0.1:0", token);
        string variable = "MISSILI_INPUT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray() };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            MessageSnapshot first = (await fixture.Runtime.ReceiveAsync(TestStore.Message("初始聊天资料"), token)).Message!;
            ConversationContextSnapshot initial = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext
            { OriginalMessage = first, CurrentContent = first.Segments }, token);
            MessageSnapshot latest = (await fixture.Runtime.ReceiveAsync(TestStore.Message("后来追加的聊天"), token)).Message!;
            ConversationContextSnapshot appended = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext
            { OriginalMessage = latest, CurrentContent = latest.Segments }, token);
            appended = appended with { Window = appended.Window with { Messages = [latest] } };
            using JsonDocument arguments = JsonDocument.Parse("{}");
            ModelRequest request = new()
            {
                Purpose = "input.order",
                Profile = initial.Profile,
                Candidates = ["A"],
                Conversation = first.Address,
                Input = new ModelInput
                {
                    Instructions = "固定系统指令",
                    Items =
                    [
                        new ConversationSliceInput { Conversation = initial },
                        new ModelTurnInput { Message = new ModelMessage { Role = "assistant", Content =
                            [new ModelBlock { Kind = ModelBlockKind.ToolCall, ToolCallId = "one", Name = "ordered_tool", Data = arguments.RootElement }] } },
                        new ModelTurnInput { Message = new ModelMessage { Role = "tool", Content =
                            [new ModelBlock { Kind = ModelBlockKind.ToolResult, ToolCallId = "one", Text = "已完成的工具结果" }] } },
                        new ConversationSliceInput { Conversation = appended }
                    ]
                }
            };
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(request, cancellationToken: token);
            Assert.True(result.Complete, result.Error);
            JsonElement body = Assert.Single(mock.Requests).Body!.Value;
            string[] values = Strings(body).ToArray();
            Assert.Single(values, value => value == "固定系统指令");
            int initialIndex = Array.IndexOf(values, "初始聊天资料");
            int callIndex = Array.IndexOf(values, "ordered_tool");
            int resultIndex = Array.IndexOf(values, "已完成的工具结果");
            int appendedIndex = Array.IndexOf(values, "后来追加的聊天");
            Assert.True(initialIndex >= 0 && initialIndex < callIndex && callIndex < resultIndex && resultIndex < appendedIndex);
            Assert.Single(values, value => value == "初始聊天资料");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>只遍历实际线上的字符串值，避免 JSON 转义方式影响顺序断言。</summary>
    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                foreach (string text in Strings(item)) yield return text;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
                foreach (string text in Strings(property.Value)) yield return text;
        }
    }
}
