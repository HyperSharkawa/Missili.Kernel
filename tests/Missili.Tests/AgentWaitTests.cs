using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实运行器、工具和协议验证等待事件，测试信号不代替生产等待实现。</summary>
public sealed class AgentWaitTests
{
    /// <summary>同树的兄弟可以等待彼此，成果会唤醒等待者而不要求父代理转发。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiblingResultAndWaiterCancellationHaveIndependentLifetimes(bool cancelWaiter)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<JsonElement> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int rootCalls = 0, waiterCalls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement request = document.RootElement;
            string model = request.GetProperty("model").GetString()!;
            if (model == "C")
            {
                await release.Task.WaitAsync(context.RequestAborted);
                await AgentCommunicationTests.RespondAsync(context, "兄弟代理的已存成果", []);
            }
            else if (model == "B")
            {
                int turn = Interlocked.Increment(ref waiterCalls);
                if (turn == 1) await AgentCommunicationTests.RespondAsync(context, null, [("list_agents", new())]);
                else if (turn == 2)
                {
                    Guid worker = Results(request).Last().GetProperty("data").GetProperty("agents").EnumerateArray()
                        .Single(agent => agent.GetProperty("template").GetString() == "worker").GetProperty("agent_id").GetGuid();
                    await AgentCommunicationTests.RespondAsync(context, null, [("wait_agents", new()
                    { ["agent_ids"] = new[] { worker }, ["timeout_seconds"] = 3600 })]);
                }
                else
                {
                    observed.TrySetResult(Results(request).Last());
                    await AgentCommunicationTests.RespondAsync(context, "兄弟成果已消费", []);
                }
            }
            else if (Interlocked.Increment(ref rootCalls) == 1)
                await AgentCommunicationTests.RespondAsync(context, null,
                [("spawn_agent", new() { ["task"] = "等候兄弟成果", ["template"] = "waiter" }),
                 ("spawn_agent", new() { ["task"] = "完成合成工作", ["template"] = "worker" }), ("finish_turn", new())]);
            else await AgentCommunicationTests.RespondAsync(context, null, [("finish_turn", new())]);
        }, token);
        string variable = "MISSILI_SIBLING_WAIT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(server.BaseAddress, variable);
            configuration = configuration with
            {
                Models = configuration.Models.Add(configuration.Models[1] with { Id = "C", Name = "C" }),
                Profiles = [configuration.Profiles[0] with { Agent = configuration.Profiles[0].Agent with
                {
                    DelegatedTools = [AgentToolIds.List, AgentToolIds.Wait],
                    Templates = [new SubagentTemplate { Id = "worker", Description = "执行工作", Instructions = "完成明确工作", Models = ["C"], Tools = [] },
                        new SubagentTemplate { Id = "waiter", Description = "等待协作", Instructions = "查询同树并等待 worker", Models = ["B"], Tools = [AgentToolIds.List, AgentToolIds.Wait] }]
                } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            AgentWakeResult admission = await WakeAsync(fixture, "启动兄弟协作", token);
            AgentTaskStatus waiting = await WaitStateAsync(fixture, state => state.WaitingAgents == 1, token);
            if (cancelWaiter)
            {
                Guid waiter = waiting.Agents.Single(agent => agent.TemplateId == "waiter").Id;
                MessageSnapshot stop = (await fixture.Runtime.ReceiveAsync(TestStore.Message("取消等待代理") with { SenderId = "operator" }, token)).Message!;
                Assert.True((await fixture.Runtime.Agents.RequestStopAsync(new AgentStopRequest { MessageId = stop.Id, AgentId = waiter, Emergency = true }, token)).Accepted);
                AgentTaskStatus stopped = await WaitStateAsync(fixture, state => state.Agents.Single(agent => agent.Id == waiter).Activity == AgentActivity.Closed, token);
                AgentSnapshot worker = stopped.Agents.Single(agent => agent.TemplateId == "worker");
                Assert.Null(worker.StopRequestedAt); Assert.Equal(AgentActivity.RunningModel, worker.Activity);
            }
            release.TrySetResult();
            if (!cancelWaiter)
            {
                JsonElement result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                Assert.Equal("result", result.GetProperty("data").GetProperty("reason").GetString());
                JsonElement artifact = Assert.Single(result.GetProperty("data").GetProperty("results").EnumerateArray());
                Assert.Equal(1, artifact.GetProperty("revision").GetInt64());
            }
            await FinishedAsync(fixture, admission.TaskId!.Value, token);
            Assert.Equal(cancelWaiter ? 2 : 3, waiterCalls);
            Assert.Empty(fixture.Transport.Requests);
        }
        finally { release.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>超时和新输入只结束当前等待，子代理仍能完成并再次唤醒主代理。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrNewInputLeavesTargetRunning(bool newInput)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<JsonElement> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int rootCalls = 0, childCalls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement request = document.RootElement;
            if (request.GetProperty("model").GetString() == "B")
            {
                Interlocked.Increment(ref childCalls);
                await release.Task.WaitAsync(context.RequestAborted);
                await AgentCommunicationTests.RespondAsync(context, "等待结束后的子成果", []);
                return;
            }
            switch (Interlocked.Increment(ref rootCalls))
            {
                case 1: await AgentCommunicationTests.RespondAsync(context, null, [("spawn_agent", new() { ["task"] = "持续合成工作" })]); break;
                case 2:
                    Guid target = Results(request).Last().GetProperty("data").GetProperty("agent_id").GetGuid();
                    await AgentCommunicationTests.RespondAsync(context, null, [("wait_agents", new() { ["agent_ids"] = new[] { target }, ["timeout_seconds"] = 5 })]);
                    break;
                case 3:
                    observed.TrySetResult(Results(request).Last());
                    await AgentCommunicationTests.RespondAsync(context, null, [("finish_turn", new())]); break;
                default: await AgentCommunicationTests.RespondAsync(context, "收到迟到成果", []); break;
            }
        }, token);
        string variable = "MISSILI_WAIT_EVENT_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock, models: Configuration(server.BaseAddress, variable), agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            AgentWakeResult admission = await WakeAsync(fixture, "等待子成果", token);
            await WaitStateAsync(fixture, state => state.WaitingAgents == 1 && state.Agents.Any(agent => agent.ParentId.HasValue && agent.Activity == AgentActivity.RunningModel), token);
            if (newInput) Assert.Equal(AgentWakeState.Queued, (await WakeAsync(fixture, "等待期间追加需求", token)).State);
            else clock.Advance(TimeSpan.FromSeconds(5));
            JsonElement result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Equal(newInput ? "new_input" : "timeout", result.GetProperty("data").GetProperty("reason").GetString());
            AgentTaskStatus active = await WaitStateAsync(fixture, state => state.WaitingAgents == 0 && state.Agents.Single(agent => agent.ParentId is null).Activity == AgentActivity.Idle, token);
            Assert.Null(active.Agents.Single(agent => agent.ParentId.HasValue).StopRequestedAt);
            release.TrySetResult();
            await FinishedAsync(fixture, admission.TaskId!.Value, token);
            Assert.Equal(1, childCalls); Assert.Equal(4, rootCalls);
            Assert.Equal("收到迟到成果", Assert.Single(fixture.Transport.Requests).Segments[0].Text);
        }
        finally { release.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>停止一个带孙代理的子树共享 180 秒，只有停止目标整理一次，主代理不被一并取消。</summary>
    [Fact]
    public async Task NestedStopSharesDeadlineAndOneSettlement()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        TaskCompletionSource grandchildEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource settlementEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int roots = 0, children = 0, settlements = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement request = document.RootElement;
            if (AgentCommunicationTests.Content(request).Contains("任务已请求停止。只整理", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref settlements); settlementEntered.TrySetResult();
                await AgentCommunicationTests.RespondAsync(context, "保留停止前的子树成果", []);
            }
            else if (request.GetProperty("model").GetString() == "A")
            {
                if (Interlocked.Increment(ref roots) == 1)
                    await AgentCommunicationTests.RespondAsync(context, null, [("spawn_agent", new() { ["task"] = "创建并管理子工作" }), ("finish_turn", new())]);
                else await AgentCommunicationTests.RespondAsync(context, null, [("finish_turn", new())]);
            }
            else if (Interlocked.Increment(ref children) == 1)
                await AgentCommunicationTests.RespondAsync(context, null, [("spawn_agent", new() { ["task"] = "长时间合成工作" }), ("finish_turn", new())]);
            else
            {
                grandchildEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            }
        }, token);
        string variable = "MISSILI_NESTED_STOP_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(server.BaseAddress, variable);
            // 同一个可控时钟也驱动协议期限；本例只触发子树 180 秒，避免先触发其他在途模型的网络期限。
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with
            {
                Execution = configuration.Profiles[0].Execution with { ConnectTimeout = TimeSpan.FromMinutes(10), HeadersTimeout = TimeSpan.FromMinutes(10), ReadIdleTimeout = TimeSpan.FromMinutes(10) },
                Agent = configuration.Profiles[0].Agent with
            {
                DelegatedTools = [AgentToolIds.Spawn, AgentToolIds.FinishTurn],
                Templates = [configuration.Profiles[0].Agent.Templates[0] with { Tools = [AgentToolIds.Spawn, AgentToolIds.FinishTurn] }]
            } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            AgentWakeResult admission = await WakeAsync(fixture, "只在子树内测试停止", token);
            await grandchildEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            AgentTaskStatus running = await WaitStateAsync(fixture, state => state.Agents.Any(agent => agent.Depth == 1 && agent.Activity == AgentActivity.Idle), token);
            Guid target = running.Agents.Single(agent => agent.Depth == 1).Id;
            MessageSnapshot stop = (await fixture.Runtime.ReceiveAsync(TestStore.Message("停止指定子树") with { SenderId = "operator" }, token)).Message!;
            Assert.True((await fixture.Runtime.Agents.RequestStopAsync(new AgentStopRequest { MessageId = stop.Id, AgentId = target }, token)).Accepted);
            AgentTaskStatus stopping = (await fixture.Runtime.Agents.GetActiveAsync(TestStore.Address(), token))!;
            Assert.Null(stopping.Agents.Single(agent => agent.Depth == 0).StopRequestedAt);
            DateTimeOffset deadline = Assert.Single(stopping.Agents.Where(agent => agent.Depth > 0).Select(agent => agent.StopDeadline!.Value).Distinct());
            Assert.Equal(clock.GetUtcNow().AddSeconds(180), deadline);
            await settlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            clock.Advance(TimeSpan.FromSeconds(179));
            Assert.NotNull(await fixture.Runtime.Agents.GetActiveAsync(TestStore.Address(), token));
            clock.Advance(TimeSpan.FromSeconds(1));
            await FinishedAsync(fixture, admission.TaskId!.Value, token);
            Assert.Equal(1, settlements); Assert.Equal(2, children);
            Assert.Empty(fixture.Transport.Requests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>共享配置目录只使用本测试合成端点，子代理未获发送工具。</summary>
    private static ConversationConfiguration Configuration(Uri address, string variable)
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address, variable, ModelProtocol.ChatCompletions);
        return configuration with
        {
            Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
            Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile
            {
                Enabled = true, Models = ["A"], Tools = [AgentToolIds.Spawn, AgentToolIds.Wait, AgentToolIds.FinishTurn], Administrators = ["operator"],
                DefaultTemplate = "worker", DelegatedTools = [],
                Templates = [new SubagentTemplate { Id = "worker", Description = "持续工作", Instructions = "完成明确任务", Models = ["B"], Tools = [] }]
            } }]
        };
    }

    /// <summary>输入先真实入库，再交宿主接单，保留同一人的身份。</summary>
    private static async Task<AgentWakeResult> WakeAsync(RuntimeFixture fixture, string text, CancellationToken token)
    {
        MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message(text), token)).Message!;
        AgentWakeResult admission = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments }, token);
        Assert.True(admission.Registered, admission.Error); return admission;
    }

    /// <summary>从实际 Chat Completions 输入读取工具结果，不复用生产编码器构造预期。</summary>
    private static JsonElement[] Results(JsonElement request) => request.GetProperty("messages").EnumerateArray()
        .Where(message => message.GetProperty("role").GetString() == "tool").Select(message =>
        {
            JsonElement content = message.GetProperty("content");
            string text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            using JsonDocument document = JsonDocument.Parse(text); return document.RootElement.Clone();
        }).ToArray();

    /// <summary>公开活动条件决定下一步操作，短延迟只让出执行。</summary>
    private static async Task<AgentTaskStatus> WaitStateAsync(RuntimeFixture fixture, Func<AgentTaskStatus, bool> predicate, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskStatus? status = await fixture.Runtime.Agents.GetActiveAsync(TestStore.Address(), deadline.Token);
            if (status is not null && predicate(status)) return status;
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>任务真正释放后才确认测试结束，不把一次模型返回当作任务完成。</summary>
    private static async Task FinishedAsync(RuntimeFixture fixture, Guid taskId, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskSnapshot task = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(TestStore.Address(), taskId, deadline.Token))!;
            if (task.ReleasedAt.HasValue) { Assert.True(task.State == AgentTaskState.Completed, task.State + ": " + task.Error); return; }
            await Task.Delay(10, deadline.Token);
        }
    }
}
