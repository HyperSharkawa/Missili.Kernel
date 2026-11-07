using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Nexus;
using Missili.OneBot.Mock;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>实际入口 DLL 只负责明确触发，完整任务继续由宿主推进。</summary>
public sealed class AgentEntryTests
{
    /// <summary>OneBot 群私聊经真实入库、入口、候选切换、内置工具、回复和回执完成任务。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneBotEntryRunsWholeAgentChain(bool group)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps = [new ModelMockStep { Model = "A", Status = 500 }, new ModelMockStep { Model = "B", Events =
                ["data: {\"id\":\"tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"B\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"list-one\",\"type\":\"function\",\"function\":{\"name\":\"list_agents\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n", "data: [DONE]\n\n"] },
                new ModelMockStep { Model = "A", Text = "Agent 全链路已完成", FragmentBytes = 1 }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_AGENT_ENTRY_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            configuration = configuration with { Profiles = [configuration.Profiles[0] with { Agent = configuration.Profiles[0].Agent with { Tools = [AgentToolIds.List] } }] };
            await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, !group, token, models: configuration, enableAgents: true);
            await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("openai"), RuntimeFixture.Configuration(), token);
            Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(EntryPackage, RuntimeFixture.Configuration(), token)).State);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "不触发 Agent 的群背景", group), token: token);
            await fixture.WaitMessageAsync("1", token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(2, "/agent 明确执行任务", group), token: token);
            MockAction reply = await fixture.WaitActionAsync(group ? "send_group_msg" : "send_private_msg", token);
            Assert.Contains("Agent 全链路已完成", string.Concat(reply.Parameters.GetProperty("message").EnumerateArray().Where(segment => segment.GetProperty("type").GetString() == "text").Select(segment => segment.GetProperty("data").GetProperty("text").GetString())));
            Assert.Equal(new[] { "A", "B", "A" }, model.Requests.Select(request => request.Model));
            ModelCallPage calls = await fixture.Runtime.Models.Calls.QueryCallsAsync(new ModelCallQuery { Purpose = "agent", Conversation = OneBotFixture.Address(group) }, token);
            Assert.Equal(2, calls.Calls.Length);
            Guid taskId = calls.Calls[0].Origin!.TaskId!.Value;
            IAgentHistory history = (IAgentHistory)fixture.Runtime.History;
            AgentTaskSnapshot task = await FinishedAsync(history, OneBotFixture.Address(group), taskId, token);
            Assert.Equal(AgentTaskState.Completed, task.State); Assert.Equal(1, task.Usage.ToolCalls);
            ImmutableArray<BatchSendResult> batches = await ((IOutgoingHistory)fixture.Runtime.History).GetTaskBatchesAsync(OneBotFixture.Address(group), taskId, 20, token);
            Assert.True(Assert.Single(batches).Confirmed);
            Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == (group ? "send_group_msg" : "send_private_msg"));
            MessageSnapshot trigger = await fixture.WaitMessageAsync("2", token);
            Assert.Equal("/agent 明确执行任务", trigger.Segments[0].Text);
            string requestContent = string.Join("\n", model.Requests[1].Body!.Value.GetProperty("messages").EnumerateArray().Select(message =>
                message.GetProperty("content").ValueKind == JsonValueKind.String ? message.GetProperty("content").GetString() : string.Concat(message.GetProperty("content").EnumerateArray().Select(block => block.GetProperty("text").GetString()))));
            Assert.Contains("不触发 Agent 的群背景", requestContent);
            Assert.Contains("明确执行任务", requestContent); Assert.DoesNotContain("/agent 明确执行任务", requestContent);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>入口重载实际回收旧 DLL，已经接单的任务继续等待模型并发送完整终答。</summary>
    [Fact]
    public async Task EntryReloadDoesNotCancelOwnedHostTask()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = [new ModelMockStep { WaitForSignal = "finish", Text = "任务由宿主持有" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_ENTRY_LIFETIME_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(model.BaseAddress, variable), agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            await fixture.Runtime.Plugins.LoadAsync(EntryPackage, RuntimeFixture.Configuration(), token); fixture.Runtime.OpenAgents();
            MessageSnapshot trigger = (await fixture.Runtime.ReceiveAsync(TestStore.Message("/agent 持续任务"), token)).Message!;
            await model.NextRequestAsync(token); await fixture.FinishedAsync(trigger.Id, token);
            Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("missili.plugins.agent-chat", EntryPackage, RuntimeFixture.Configuration(), token)).State);
            WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("missili.plugins.agent-chat")!;
            for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
            Assert.False(reference.IsAlive);
            AgentTaskStatus active = (await fixture.Runtime.Agents.GetActiveAsync(TestStore.Address(), token))!;
            model.Release("finish");
            await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "任务由宿主持有", token);
            await FinishedAsync((IAgentHistory)fixture.Runtime.History, TestStore.Address(), active.Task.Id, token);
            Assert.Equal(1, model.TotalRequests);
        }
        finally { model.Release("finish"); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>仅提及也进入决策；未启用其他规则时，其他提及、卡片、未知段和自身上报不触发。</summary>
    [Theory]
    [InlineData("mention", true)]
    [InlineData("other", false)]
    [InlineData("unknown", false)]
    [InlineData("card", false)]
    [InlineData("self", false)]
    public async Task OnlyRealMentionCanWakeWithoutBody(string kind, bool wake)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = [new ModelMockStep { Text = "提及已进入决策" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_ENTRY_TRIGGER_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(model.BaseAddress, variable), agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            await fixture.Runtime.Plugins.LoadAsync(EntryPackage, RuntimeFixture.Configuration(), token); fixture.Runtime.OpenAgents();
            MessageSegment mention = SegmentValues.Mention(kind == "other" ? "another" : TestStore.Address().BotAccountId);
            IncomingSegment segment = new() { Kind = mention.Kind, Payload = mention.Payload };
            if (kind is "card" or "unknown") segment = new IncomingSegment
            {
                Kind = kind == "card" ? SegmentTypes.Json : "synthetic.unknown",
                ParseState = kind == "card" ? SegmentParseState.Parsed : SegmentParseState.Unrecognized,
                Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = "/agent @bot 隐藏内容" })
            };
            IncomingMessage incoming = TestStore.Message() with { Segments = [segment], Direction = kind == "self" ? MessageDirection.Outgoing : MessageDirection.Incoming };
            ReceiveReceipt received = await fixture.Runtime.ReceiveAsync(incoming, token);
            await fixture.FinishedAsync(received.Message!.Id, token);
            if (wake) await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "提及已进入决策", token);
            Assert.Equal(wake ? 1 : 0, model.TotalRequests);
            Assert.Equal(wake ? 1 : 0, fixture.Transport.Requests.Count);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static string EntryPackage => OfficialPluginPackages.GetPath("agent-chat");

    /// <summary>仅启用合成配置档，模型目录仍通过框架统一加载。</summary>
    private static ConversationConfiguration Configuration(Uri address, string variable)
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address, variable, ModelProtocol.ChatCompletions);
        return configuration with
        {
            Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
            Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile { Enabled = true, Models = ["A", "B"] } }]
        };
    }

    /// <summary>发送意图出现还不代表任务关闭，等待真实释放归属的记录。</summary>
    private static async Task<AgentTaskSnapshot> FinishedAsync(IAgentHistory history, MessageAddress address, Guid taskId, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskSnapshot task = (await history.GetTaskAsync(address, taskId, deadline.Token))!;
            if (task.ReleasedAt.HasValue) return task;
            await Task.Delay(10, deadline.Token);
        }
    }
}
