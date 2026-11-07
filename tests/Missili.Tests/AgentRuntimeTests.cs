using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实宿主 Agent 从接单自主推进到模型、工具及发送，测试不手工代替循环。</summary>
public sealed class AgentRuntimeTests
{
    /// <summary>整组预算不足时零工具执行，但已取得的完整模型输出仍留在任务记录中。</summary>
    [Fact]
    public async Task ToolBudgetRejectionPreservesModelOutput()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse("""{"id":"budget","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"one","type":"function","function":{"name":"list_agents","arguments":"{}"}},{"id":"two","type":"function","function":{"name":"finish_turn","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { Response = response.RootElement.Clone() }], new AgentProfile
            { Enabled = true, Stream = false, Models = ["A"], Tools = [AgentToolIds.List, AgentToolIds.FinishTurn], Budget = new AgentBudget { ToolCalls = 1 } }, token);
        session.Runtime.OpenAgents();
        AgentWakeResult accepted = await session.WakeAsync("预算内执行", "initiator", token);
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(AgentTaskState.Failed, task.State); Assert.Equal(0, task.Usage.ToolCalls); Assert.Equal(1, task.Usage.ModelCalls);
        AgentEntryPage entries = await session.History.ReadEntriesAsync(TestStore.Address(), task.Id, task.RootAgentId, 0, 100, token);
        AgentEntry output = Assert.Single(entries.Entries, entry => entry.Kind == AgentEntryKind.ModelOutput);
        Assert.Equal(2, JsonSerializer.Deserialize<ModelResult>(output.Data)!.Content.Count(block => block.Kind == ModelBlockKind.ToolCall));
        Assert.Empty(session.Fixture.Transport.Requests); Assert.Equal(1, session.Mock.TotalRequests);
    }

    /// <summary>三协议都由运行器自主闭合工具组；direct 只发送完整终答，伴随文本不发布。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, true)]
    [InlineData(ModelProtocol.ChatCompletions, false)]
    [InlineData(ModelProtocol.Responses, true)]
    [InlineData(ModelProtocol.Responses, false)]
    [InlineData(ModelProtocol.Anthropic, true)]
    [InlineData(ModelProtocol.Anthropic, false)]
    public async Task RuntimeCompletesRealToolLoop(ModelProtocol protocol, bool stream)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol).Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal));
        ModelMockStep first = new()
        {
            Response = response.RootElement.Clone(),
            Events = ModelProtocolRoundTripTests.Events(protocol, response.RootElement)
            .Select(frame => frame.Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal)).ToImmutableArray()
        };
        await using Session session = await Session.CreateAsync(protocol, [first, new ModelMockStep { Text = "完成了真实多轮任务" }],
            new AgentProfile { Enabled = true, Models = ["A"], Stream = stream, Tools = ["fixture.tool"] }, token);
        string tool = await session.Fixture.PackageAsync("tool", "v1", token);
        await session.Runtime.Plugins.LoadAsync(tool, RuntimeFixture.Configuration("tool"), token);
        session.Runtime.OpenAgents();
        await session.Runtime.ReceiveAsync(TestStore.Message("群内背景资料"), token);
        AgentWakeResult accepted = await session.WakeAsync("明确任务", "initiator", token);
        Assert.Equal(AgentWakeState.Created, accepted.State);
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(AgentTaskState.Completed, task.State);
        Assert.Equal(2, task.Usage.ModelCalls); Assert.Equal(1, task.Usage.ToolCalls);
        Assert.Equal("完成了真实多轮任务", Assert.Single(session.Fixture.Transport.Requests).Segments[0].Text);
        Assert.Equal(2, session.Mock.TotalRequests);
        Assert.All(session.Mock.Requests, request => Assert.Equal(stream, request.Streaming));
        AgentEntryPage entries = await session.History.ReadEntriesAsync(TestStore.Address(), task.Id, task.RootAgentId, 0, 100, token);
        Assert.Equal(2, entries.Entries.Count(entry => entry.Kind == AgentEntryKind.ModelOutput));
        Assert.Single(entries.Entries, entry => entry.Kind == AgentEntryKind.ToolResults);
        Assert.True(session.Runtime.IsReady);
    }

    /// <summary>输出模式不因回复插件缺失回退，普通群消息不会触发模型。</summary>
    [Fact]
    public async Task ToolsOnlyWithoutReplyPluginKeepsFinalInternal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions, [new ModelMockStep { Text = "内部结果" }],
            new AgentProfile { Enabled = true, Models = ["A"], OutputMode = AgentOutputMode.ToolsOnly }, token);
        session.Runtime.OpenAgents();
        ReceiveReceipt ordinary = await session.Runtime.ReceiveAsync(TestStore.Message("普通群消息"), token);
        await session.Fixture.FinishedAsync(ordinary.Message!.Id, token);
        Assert.Equal(0, session.Mock.TotalRequests);
        AgentWakeResult accepted = await session.WakeAsync("明确提交", "initiator", token);
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Empty(session.Fixture.Transport.Requests);
        Assert.Equal("内部结果", (await session.History.GetAgentsAsync(TestStore.Address(), task.Id, token)).Single().Result!.Content[0].Text);
    }

    /// <summary>并发补充只有一个主代理，重入返回原接单；普通成员没有停止权限。</summary>
    [Fact]
    public async Task ConcurrentInputsShareRootAndRespectControlIdentity()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { WaitForSignal = "first", Text = "第一轮" }, new ModelMockStep { Text = "处理追加要求" }],
            new AgentProfile { Enabled = true, Models = ["A"] }, token);
        session.Runtime.OpenAgents();
        MessageSnapshot initial = (await session.Runtime.ReceiveAsync(TestStore.Message("初始需求") with { SenderId = "initiator" }, token)).Message!;
        AgentWakeRequest request = new() { Cause = TestStore.WakeCause, MessageId = initial.Id, CurrentContent = initial.Segments };
        AgentWakeResult accepted = await session.Runtime.Agents.WakeAsync(request, token);
        await session.Mock.NextRequestAsync(token);
        AgentWakeResult duplicate = await session.Runtime.Agents.WakeAsync(request, token);
        Assert.Equal(AgentWakeState.Duplicate, duplicate.State);
        Assert.Equal(accepted.TaskId, duplicate.TaskId);
        AgentWakeResult[] more = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => session.WakeAsync("追加 " + index, "member-" + index, token)));
        Assert.All(more, item => { Assert.Equal(AgentWakeState.Queued, item.State); Assert.Equal(accepted.AgentId, item.AgentId); });
        MessageSnapshot stop = (await session.Runtime.ReceiveAsync(TestStore.Message("停止") with { SenderId = "member-0" }, token)).Message!;
        Assert.False((await session.Runtime.Agents.RequestStopAsync(new AgentStopRequest { MessageId = stop.Id }, token)).Accepted);
        Assert.False((await session.Runtime.Agents.RequestStopAsync(new AgentStopRequest { MessageId = initial.Id }, token)).Accepted);
        Assert.Equal(AgentWakeState.Queued, (await session.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = stop.Id, CurrentContent = stop.Segments }, token)).State);
        session.Mock.Release("first");
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(2, task.Usage.ModelCalls);
        Assert.Equal(2, session.Fixture.Transport.Requests.Count);
        string second = session.Mock.Requests[1].Body!.Value.GetRawText();
        Assert.Contains("member-0", second); Assert.Contains("member-3", second);
    }

    /// <summary>两个子代理实际同时运行，主代理结束轮次后仍保持会话归属并由成果再次唤醒。</summary>
    [Fact]
    public async Task ParallelChildrenWakeIdleParentWithoutClosingTree()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument spawn = JsonDocument.Parse(SpawnTwo);
        ModelMockStep first = ChatTools(spawn.RootElement);
        AgentProfile profile = new()
        {
            Enabled = true,
            Models = ["A"],
            Tools = [AgentToolIds.Spawn, AgentToolIds.FinishTurn],
            DefaultTemplate = "worker",
            DelegatedTools = [],
            Templates = [new SubagentTemplate { Id = "worker", Description = "并行工作", Instructions = "用户预设的子代理指令", Models = ["B"], Tools = [] }]
        };
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [first, new ModelMockStep { Model = "B", WaitForSignal = "first", Text = "子代理第一份成果" },
                new ModelMockStep { Model = "B", WaitForSignal = "second", Text = "子代理第二份成果" },
                new ModelMockStep { Model = "A", Text = "已经消费先返回的成果" }, new ModelMockStep { Model = "A", Text = "全部完成" }], profile, token);
        session.Runtime.OpenAgents();
        AgentWakeResult accepted = await session.WakeAsync("拆成两份并行处理", "initiator", token);
        await session.Mock.NextRequestAsync(token); await session.Mock.NextRequestAsync(token); await session.Mock.NextRequestAsync(token);
        AgentTaskStatus state = await WaitStatusAsync(session, status => status.Agents.Count(agent => agent.Activity == AgentActivity.RunningModel) == 2 && status.Agents.Single(agent => agent.ParentId is null).Activity == AgentActivity.Idle, token);
        Assert.Equal(2, state.ActiveWork); Assert.Equal(3, state.Agents.Length);
        Assert.All(session.Mock.Requests.Where(request => request.Model == "B"), request =>
        {
            JsonElement system = Assert.Single(request.Body!.Value.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "system");
            Assert.Equal("用户预设的子代理指令", system.GetProperty("content").GetString());
        });
        Assert.Empty(session.Fixture.Transport.Requests);
        session.Mock.Release("second");
        ModelMockRequest resumed = await session.Mock.NextRequestAsync(token);
        Assert.Equal("A", resumed.Model);
        await session.Fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "已经消费先返回的成果", token);
        session.Mock.Release("first");
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(AgentTaskState.Completed, task.State);
        Assert.Equal(5, task.Usage.ModelCalls); Assert.Equal(3, task.Usage.ToolCalls); Assert.Equal(3, task.Usage.TotalAgents);
        Assert.Equal(2, session.Fixture.Transport.Requests.Count);
        Assert.All((await session.History.GetAgentsAsync(TestStore.Address(), task.Id, token)), agent => Assert.Equal(AgentActivity.Closed, agent.Activity));
    }

    /// <summary>普通停止允许当前结果完成并最多整理一次；紧急取消没有额外模型请求。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopKeepsResultsAndEmergencyNeverSettles(bool emergency)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { WaitForSignal = "first", Text = "已经完成部分工作" }, new ModelMockStep { Text = "停止时整理已有成果" }],
            new AgentProfile { Enabled = true, Models = ["A"], Administrators = ["operator"] }, token);
        session.Runtime.OpenAgents();
        AgentWakeResult accepted = await session.WakeAsync("持续任务", "initiator", token);
        await session.Mock.NextRequestAsync(token);
        MessageSnapshot stop = (await session.Runtime.ReceiveAsync(TestStore.Message("停止任务") with { SenderId = "operator" }, token)).Message!;
        Assert.True((await session.Runtime.Agents.RequestStopAsync(new AgentStopRequest { MessageId = stop.Id, Emergency = emergency }, token)).Accepted);
        session.Mock.Release("first");
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(AgentTaskState.Cancelled, task.State);
        Assert.Equal(emergency ? 1 : 2, session.Mock.TotalRequests);
        Assert.Empty(session.Fixture.Transport.Requests);
        if (!emergency)
        {
            Assert.False(session.Mock.Requests[1].Body!.Value.TryGetProperty("tools", out JsonElement unused));
            Assert.Equal("停止时整理已有成果", (await session.History.GetAgentsAsync(TestStore.Address(), task.Id, token)).Single().Result!.Content[0].Text);
        }
    }

    /// <summary>一百八十秒使用可控时钟触发实际取消，重复停止不会延长截止时间。</summary>
    [Fact]
    public async Task GracefulStopHasOneShared180SecondDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { WaitForSignal = "first" }], new AgentProfile { Enabled = true, Models = ["A"], Administrators = ["operator"] }, token, clock);
        session.Runtime.OpenAgents();
        AgentWakeResult accepted = await session.WakeAsync("持续任务", "initiator", token);
        await session.Mock.NextRequestAsync(token);
        MessageSnapshot stop = (await session.Runtime.ReceiveAsync(TestStore.Message("停止") with { SenderId = "operator" }, token)).Message!;
        AgentStopRequest request = new() { MessageId = stop.Id };
        await session.Runtime.Agents.RequestStopAsync(request, token);
        clock.Advance(TimeSpan.FromSeconds(179));
        await session.Runtime.Agents.RequestStopAsync(request, token);
        Assert.NotNull(await session.Runtime.Agents.GetActiveAsync(TestStore.Address(), token));
        clock.Advance(TimeSpan.FromSeconds(1));
        AgentTaskSnapshot task = await session.FinishedAsync(accepted.TaskId!.Value, token);
        Assert.Equal(AgentTaskState.Cancelled, task.State);
        Assert.Equal(1, session.Mock.TotalRequests);
        Assert.Empty(session.Fixture.Transport.Requests);
    }

    /// <summary>状态条件决定继续时机，短延迟只让出执行；期限是测试失败上界。</summary>
    [Fact]
    public async Task HostShutdownCancelsActualModelWithoutFallbackOrSettlement()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { WaitForSignal = "first" }], new AgentProfile { Enabled = true, Models = ["A", "B"] }, token);
        session.Runtime.OpenAgents();
        AgentWakeResult accepted = await session.WakeAsync("运行中关闭宿主", "initiator", token);
        await session.Mock.NextRequestAsync(token);
        await session.Runtime.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
        AgentTaskSnapshot task = (await session.History.GetTaskAsync(TestStore.Address(), accepted.TaskId!.Value, token))!;
        Assert.Equal(AgentTaskState.Interrupted, task.State); Assert.NotNull(task.ReleasedAt);
        Assert.Equal(1, session.Mock.TotalRequests);
        Assert.Empty(session.Fixture.Transport.Requests);
    }

    /// <summary>状态条件决定继续时机，短延迟只让出执行；期限是测试失败上界。</summary>
    private static async Task<AgentTaskStatus> WaitStatusAsync(Session session, Func<AgentTaskStatus, bool> condition, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskStatus? status = await session.Runtime.Agents.GetActiveAsync(TestStore.Address(), deadline.Token);
            if (status is not null && condition(status)) return status;
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>独立 SSE 响应携带完整工具组，不使用生产请求编码器。</summary>
    private static ModelMockStep ChatTools(JsonElement response)
    {
        JsonElement message = response.GetProperty("choices")[0].GetProperty("message");
        Dictionary<string, object>[] tools = message.GetProperty("tool_calls").EnumerateArray().Select((call, index) => new Dictionary<string, object>
        { ["index"] = index, ["id"] = call.GetProperty("id").GetString()!, ["type"] = "function", ["function"] = call.GetProperty("function").Clone() }).ToArray();
        string frame = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "chat-tools",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "A",
            ["choices"] = new[] { new Dictionary<string, object> { ["index"] = 0, ["delta"] = new Dictionary<string, object> { ["tool_calls"] = tools }, ["finish_reason"] = "tool_calls" } }
        });
        return new ModelMockStep { Response = response.Clone(), Events = ["data: " + frame + "\n\n", "data: [DONE]\n\n"] };
    }

    private const string SpawnTwo = """{"id":"chat-spawn","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"first","type":"function","function":{"name":"spawn_agent","arguments":"{\"task\":\"第一份工作\"}"}},{"id":"second","type":"function","function":{"name":"spawn_agent","arguments":"{\"task\":\"第二份工作\"}"}},{"id":"finish","type":"function","function":{"name":"finish_turn","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";

    /// <summary>隔离模型、SQLite 与真实插件；只在插件加载完成后由测试开放 Agent。</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly string _variable;
        public RuntimeFixture Fixture { get; }
        public Missili.Kernel.InfrastructureRuntime Runtime => Fixture.Runtime;
        public IAgentHistory History => (IAgentHistory)Runtime.History;
        public ModelMockServer Mock { get; }
        /// <summary>统一拥有测试资源及合成环境变量。</summary>
        private Session(RuntimeFixture fixture, ModelMockServer mock, string variable) { Fixture = fixture; Mock = mock; _variable = variable; }
        /// <summary>使用生产配置和 Agent 模块，不注入模拟推进器。</summary>
        public static async Task<Session> CreateAsync(ModelProtocol protocol, ImmutableArray<ModelMockStep> steps, AgentProfile agent, CancellationToken token, TimeProvider? clock = null)
        {
            ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = steps }, "http://127.0.0.1:0", token);
            string variable = "MISSILI_AGENT_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(), Profiles = [configuration.Profiles[0] with { Agent = agent }] };
            RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, clock: clock, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            return new Session(fixture, mock, variable);
        }
        /// <summary>明确提交先走真实消息入库，再调用 Agent 接单。</summary>
        public async Task<AgentWakeResult> WakeAsync(string text, string sender, CancellationToken token)
        {
            MessageSnapshot message = (await Runtime.ReceiveAsync(TestStore.Message(text) with { SenderId = sender }, token)).Message!;
            return await Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments }, token);
        }
        /// <summary>读取实际释放标记作为完成条件，不用发送发生代替整个任务收尾。</summary>
        public async Task<AgentTaskSnapshot> FinishedAsync(Guid id, CancellationToken token)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                AgentTaskSnapshot task = (await History.GetTaskAsync(TestStore.Address(), id, deadline.Token))!;
                if (task.ReleasedAt.HasValue) return task;
                await Task.Delay(10, deadline.Token);
            }
        }
        /// <summary>清理前释放全部测试信号，随后等待实际资源退出。</summary>
        public async ValueTask DisposeAsync()
        {
            Mock.Release("first"); Mock.Release("second");
            try { await Fixture.DisposeAsync(); await Mock.DisposeAsync(); }
            finally { Environment.SetEnvironmentVariable(_variable, null); }
        }
    }
}
