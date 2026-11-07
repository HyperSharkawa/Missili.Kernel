using System.Text.Json;
using Missili.Abstractions;
using Missili.OneBot.Mock;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 OneBot、模型协议、动态插件和 SQLite 的完整联调。</summary>
public sealed class ModelEndToEndTests
{
    /// <summary>不同连接布局及群私聊均经过模型候选切换，并只发送一次完整回复。</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    public async Task OneBotToModelToReplyRecordsTheWholeChain(bool reverse, bool split, bool group)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario
        { StrictSteps = true, Steps = [new ModelMockStep { Model = "A", Status = 500 }, new ModelMockStep { Model = "B", Text = "完整模型回复", FragmentBytes = 1 }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_E2E_MODEL_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ConversationConfigurationTests.Configuration(new Uri(model.BaseAddress, "/v1"), variable);
            await using OneBotFixture fixture = await OneBotFixture.CreateAsync(reverse, split, token, models: config);
            PluginStatus provider = await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("openai"), RuntimeFixture.Configuration(), token);
            Assert.Equal(PluginState.Active, provider.State);
            JsonElement allowed = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["allowedConversations"] = new[] { OneBotFixture.Address(group) } }, JsonSerializerOptions.Web);
            PluginStatus sample = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples/model"), allowed, token);
            Assert.Equal(PluginState.Active, sample.State);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "未触发模型的背景消息", group), token: token);
            await fixture.WaitMessageAsync("1", token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(2, "/model 当前请求", group), token: token);
            MockAction reply = await fixture.WaitActionAsync(group ? "send_group_msg" : "send_private_msg", token);
            Assert.Contains("完整模型回复", string.Concat(Strings(reply.Parameters)));
            Assert.Equal(2, model.TotalRequests);
            Assert.Contains("未触发模型的背景消息", string.Concat(Strings(model.Requests[1].Body!.Value)));
            Assert.Contains("当前请求", string.Concat(Strings(model.Requests[1].Body!.Value)));
            Assert.DoesNotContain("/model 当前请求", string.Concat(Strings(model.Requests[1].Body!.Value)));
            MessageSnapshot trigger = await fixture.WaitMessageAsync("2", token);
            Assert.Equal("/model 当前请求", trigger.Segments[0].Text);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            HistoryPage history;
            do
            {
                history = await fixture.Runtime.History.QueryAsync(TestStore.Query(), deadline.Token);
                if (!history.Messages.Any(item => item.Direction == MessageDirection.Outgoing && item.Delivery == DeliveryState.Confirmed)) await Task.Delay(1, deadline.Token);
            } while (!history.Messages.Any(item => item.Direction == MessageDirection.Outgoing && item.Delivery == DeliveryState.Confirmed));
            Assert.Single(history.Messages, item => item.Direction == MessageDirection.Outgoing);
            Assert.Single(fixture.Peer.RecordedActions, item => item.GetProperty("action").GetString() == (group ? "send_group_msg" : "send_private_msg"));
            ModelCallRecord call = Assert.Single((await fixture.Runtime.Models.Calls.QueryCallsAsync(new ModelCallQuery { Purpose = "reply" }, token)).Calls);
            Assert.Equal(ModelCallState.Succeeded, call.State); Assert.Equal(trigger.Id, call.TriggerMessageId);
            Assert.Equal(new[] { ModelCallState.Failed, ModelCallState.Succeeded }, call.Attempts.Select(item => item.State));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>递归读取协议文本字段，测试不依赖生产序列化格式的转义细节。</summary>
    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) foreach (string text in Strings(item)) yield return text;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in value.EnumerateObject()) foreach (string text in Strings(property.Value)) yield return text;
    }
}
