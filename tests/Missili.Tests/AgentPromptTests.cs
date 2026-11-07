using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>实际配置文件与三协议请求验证默认人设、完整替换和运行事实。</summary>
public sealed class AgentPromptTests
{
    /// <summary>默认提示省略时来自唯一资源；自定义与空字符串都不能混入默认人格。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, "default", false)]
    [InlineData(ModelProtocol.Responses, "default", false)]
    [InlineData(ModelProtocol.Anthropic, "default", false)]
    [InlineData(ModelProtocol.ChatCompletions, "custom", false)]
    [InlineData(ModelProtocol.Responses, "custom", false)]
    [InlineData(ModelProtocol.Anthropic, "custom", false)]
    [InlineData(ModelProtocol.ChatCompletions, "empty", false)]
    [InlineData(ModelProtocol.Responses, "empty", false)]
    [InlineData(ModelProtocol.Anthropic, "empty", false)]
    [InlineData(ModelProtocol.ChatCompletions, "default", true)]
    public async Task ConfiguredPersonaIsTheOnlySystemInstruction(ModelProtocol protocol, string mode, bool privateChat)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = [new ModelMockStep { Text = "这次不接这个话题。" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_PERSONA_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile { Enabled = true, Models = ["A"] } }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            string file = Path.Combine(fixture.Root, "model-config", "profiles", "0.json");
            JsonObject profile = JsonNode.Parse(await File.ReadAllTextAsync(file, token))!.AsObject();
            JsonObject agent = profile["agent"]!.AsObject();
            string expected = mode == "default" ? AgentDefaults.Instructions : mode == "custom" ? "这是用户完整设定的合成角色。" : "";
            if (mode == "default") agent.Remove("instructions"); else agent["instructions"] = expected;
            await File.WriteAllTextAsync(file, profile.ToJsonString(), token);
            await fixture.Runtime.Profiles.ReloadAsync(token);
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token); fixture.Runtime.OpenAgents();
            IncomingMessage incoming = TestStore.Message("普通聊天内容");
            if (privateChat) incoming = incoming with { Address = incoming.Address with { ConversationKind = "private" } };
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(incoming, token)).Message!;
            AgentWakeResult wake = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { MessageId = source.Id, CurrentContent = source.Segments, Cause = TestStore.WakeCause }, token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            AgentTaskSnapshot? task;
            do { task = await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(source.Address, wake.TaskId!.Value, deadline.Token); if (task!.ReleasedAt is null) await Task.Delay(10, deadline.Token); } while (task!.ReleasedAt is null);
            Assert.Equal(AgentTaskState.Completed, task.State); Assert.Equal(1, mock.TotalRequests);
            JsonElement request = Assert.Single(mock.Requests).Body!.Value;
            string system;
            string allText;
            if (protocol == ModelProtocol.Anthropic)
            {
                system = request.TryGetProperty("system", out JsonElement value) ? Text(value) : "";
                allText = string.Join("\n", request.GetProperty("messages").EnumerateArray().Select(message => Text(message.GetProperty("content"))));
            }
            else
            {
                JsonElement[] messages = request.GetProperty(protocol == ModelProtocol.Responses ? "input" : "messages").EnumerateArray().ToArray();
                system = string.Join("\n", messages.Where(message => message.GetProperty("role").GetString() == "system").Select(message => Text(message.GetProperty("content"))));
                allText = string.Join("\n", messages.Where(message => message.GetProperty("role").GetString() != "system").Select(message => Text(message.GetProperty("content"))));
            }
            Assert.Equal(expected, system);
            Assert.Contains("\"output_mode\":\"direct\"", allText);
            Assert.Contains(privateChat ? "\"ConversationKind\":\"private\"" : "\"ConversationKind\":\"group\"", allText);
            Assert.Contains("普通聊天内容", allText);
            Assert.DoesNotContain("明确任务输入来源", allText);
            Assert.Equal("这次不接这个话题。", Assert.Single(fixture.Transport.Requests).Segments[0].Text);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>读取独立协议捕获中的真实文本，不将生产编码器用于生成预期请求。</summary>
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!
        : string.Concat(value.EnumerateArray().Where(block => block.TryGetProperty("text", out _)).Select(block => block.GetProperty("text").GetString()));
}
