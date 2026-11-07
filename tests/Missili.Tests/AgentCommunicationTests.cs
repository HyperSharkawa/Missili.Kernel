using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>响应使用刚返回的真实代理身份，验证投递、唤醒、等待和成果消费。</summary>
public sealed class AgentCommunicationTests
{
    /// <summary>所有实际执行额度为一时，主代理等待不会阻塞子代理；等待消费后不重复追加成果通知。</summary>
    [Fact]
    public async Task MessagingFollowupAndWaitWorkWithSingleExecutionSlot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<JsonElement> rootRequests = new();
        ConcurrentQueue<JsonElement> childRequests = new();
        int rootCalls = 0, childCalls = 0;
        Guid childId = Guid.Empty;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement request = document.RootElement;
            if (request.GetProperty("model").GetString() == "B")
            {
                childRequests.Enqueue(request.Clone());
                int turn = Interlocked.Increment(ref childCalls);
                if (turn == 1) await firstRelease.Task.WaitAsync(context.RequestAborted);
                else { secondStarted.TrySetResult(); await secondRelease.Task.WaitAsync(context.RequestAborted); }
                await RespondAsync(context, turn == 1 ? "第一轮子成果" : "第二轮子成果", []);
                return;
            }
            rootRequests.Enqueue(request.Clone());
            switch (Interlocked.Increment(ref rootCalls))
            {
                case 1:
                    await RespondAsync(context, null, [("spawn_agent", new Dictionary<string, object> { ["task"] = "完成第一轮工作" }), ("finish_turn", new Dictionary<string, object>())]);
                    break;
                case 2:
                    childId = SpawnedId(request);
                    await RespondAsync(context, null, [("send_agent_message", new Dictionary<string, object> { ["agent_id"] = childId, ["message"] = "暂存这条通信，不单独启动" })]);
                    break;
                case 3:
                    Assert.Equal(1, Volatile.Read(ref childCalls));
                    await RespondAsync(context, null,
                    [
                        ("followup_agent", new Dictionary<string, object> { ["agent_id"] = childId, ["task"] = "请完成第二轮工作" }),
                        ("wait_agents", new Dictionary<string, object> { ["agent_ids"] = new[] { childId }, ["timeout_seconds"] = 30, ["after_revisions"] = new Dictionary<string, long> { [childId.ToString()] = 0 } })
                    ]);
                    break;
                default: await RespondAsync(context, "已取得第二轮成果", []); break;
            }
        }, token);
        string variable = "MISSILI_COMMUNICATION_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(server.BaseAddress, variable, ModelProtocol.ChatCompletions);
            configuration = configuration with
            {
                Providers = [configuration.Providers[0] with { MaxConcurrency = 1 }],
                Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile
                {
                    Enabled = true, Models = ["A"], Tools = [AgentToolIds.Spawn, AgentToolIds.Message, AgentToolIds.Followup, AgentToolIds.Wait, AgentToolIds.FinishTurn],
                    DefaultTemplate = "worker", DelegatedTools = [], Budget = new AgentBudget { WorkingAgents = 1 },
                    Templates = [new SubagentTemplate { Id = "worker", Description = "合成工作", Models = ["B"], Instructions = "完成明确任务并读取通信。", Tools = [] }]
                } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration,
                modelOptions: new ModelOptions { NetworkConcurrency = 1, QueueCapacity = 1, TranscriptionConcurrency = 1, TranscriptionCapacity = 1 },
                agentOptions: new AgentOptions { Enabled = true },
                pluginExecutionOptions: new PluginExecutionOptions { ToolsAndProcessors = new PluginToolProcessorExecutionOptions { Concurrency = 1, PerPluginConcurrency = 1, QueueCapacity = 1 } });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message("执行通信与等待任务"), token)).Message!;
            AgentWakeResult task = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments }, token);
            await WaitStateAsync(fixture, status => status.Agents.Any(agent => agent.ParentId.HasValue && agent.Activity == AgentActivity.RunningModel), token);
            firstRelease.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            AgentTaskStatus waiting = await WaitStateAsync(fixture, status => status.WaitingAgents == 1, token);
            Assert.Equal(1, waiting.ActiveWork);
            AgentRuntimeCounts counts = await fixture.Runtime.GetAgentCountsAsync(token);
            Assert.Equal(1, counts.ActiveTasks); Assert.Equal(1, counts.WorkingAgents); Assert.Equal(1, counts.WaitingAgents);
            Assert.Equal(3, Volatile.Read(ref rootCalls));
            Assert.Contains("暂存这条通信", Content(childRequests.Last()));
            Assert.Contains("请完成第二轮工作", Content(childRequests.Last()));
            secondRelease.TrySetResult();
            await fixture.Transport.WaitForAsync(sent => sent.Segments[0].Text == "已取得第二轮成果", token);
            AgentTaskSnapshot finished = await FinishedAsync(fixture, task.TaskId!.Value, token);
            Assert.Equal(AgentTaskState.Completed, finished.State);
            Assert.Equal(6, finished.Usage.ModelCalls);
            Assert.Equal(4, Volatile.Read(ref rootCalls)); Assert.Equal(2, Volatile.Read(ref childCalls));
            JsonElement last = rootRequests.Last();
            JsonElement waitingResult = last.GetProperty("messages").EnumerateArray().Where(item => item.GetProperty("role").GetString() == "tool")
                .Select(item => ParseContent(item)).Last();
            Assert.Equal("result", waitingResult.GetProperty("data").GetProperty("reason").GetString());
            Assert.Equal(2, waitingResult.GetProperty("data").GetProperty("results")[0].GetProperty("revision").GetInt64());
            Assert.DoesNotContain(last.GetProperty("messages").EnumerateArray(), item => item.GetProperty("role").GetString() == "user" && ContentOf(item).Contains("第二轮子成果", StringComparison.Ordinal));
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>从真实工具结果解析动态子代理身份，不约定宿主 Guid 的生成方式。</summary>
    private static Guid SpawnedId(JsonElement request)
    {
        foreach (JsonElement message in request.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() != "tool") continue;
            JsonElement result = ParseContent(message);
            if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("agent_id", out JsonElement id)) return id.GetGuid();
        }
        throw new InvalidOperationException("没有收到 spawn_agent 的实际结果。");
    }

    /// <summary>解析 SDK 发送的实际工具结果文本，预期结构独立于生产编码器。</summary>
    private static JsonElement ParseContent(JsonElement message)
    {
        using JsonDocument document = JsonDocument.Parse(ContentOf(message)); return document.RootElement.Clone();
    }

    /// <summary>协议允许字符串或文本块，两种形式都只读取真实正文。</summary>
    private static string ContentOf(JsonElement message)
    {
        JsonElement content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Where(item => item.TryGetProperty("text", out JsonElement unused)).Select(item => item.GetProperty("text").GetString()));
    }

    /// <summary>将正文中的 JSON 数据展开用于核对中文通信内容。</summary>
    internal static string Content(JsonElement request) => string.Join("\n", request.GetProperty("messages").EnumerateArray().Select(message =>
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null) return "";
        string text = ContentOf(message);
        if (!text.StartsWith('{')) return text;
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("text", out JsonElement body) ? body.GetString()! : text;
    }));

    /// <summary>按公开 Chat Completions SSE 格式写入一次完整响应。</summary>
    internal static async Task RespondAsync(HttpContext context, string? text, (string Name, Dictionary<string, object> Arguments)[] tools)
    {
        context.Response.ContentType = "text/event-stream";
        Dictionary<string, object?> delta = new();
        if (text is not null) delta["content"] = text;
        if (tools.Length > 0) delta["tool_calls"] = tools.Select((tool, index) => new Dictionary<string, object>
        { ["index"] = index, ["id"] = Guid.NewGuid().ToString("N"), ["type"] = "function", ["function"] = new Dictionary<string, object> { ["name"] = tool.Name, ["arguments"] = JsonSerializer.Serialize(tool.Arguments) } }).ToArray();
        string frame = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "chat",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "synthetic",
            ["choices"] = new[] { new Dictionary<string, object> { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = tools.Length > 0 ? "tool_calls" : "stop" } }
        });
        await context.Response.WriteAsync("data: " + frame + "\n\ndata: [DONE]\n\n", context.RequestAborted);
    }

    /// <summary>以真实活动条件观察等待登记，延迟只让出线程，不决定测试顺序。</summary>
    private static async Task<AgentTaskStatus> WaitStateAsync(RuntimeFixture fixture, Func<AgentTaskStatus, bool> condition, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskStatus? status = await fixture.Runtime.Agents.GetActiveAsync(TestStore.Address(), deadline.Token);
            if (status is not null && condition(status)) return status;
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>等待真实会话归属释放，避免把已发消息误当作整个任务关闭。</summary>
    private static async Task<AgentTaskSnapshot> FinishedAsync(RuntimeFixture fixture, Guid id, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskSnapshot task = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(TestStore.Address(), id, deadline.Token))!;
            if (task.ReleasedAt.HasValue) return task;
            await Task.Delay(10, deadline.Token);
        }
    }
}
