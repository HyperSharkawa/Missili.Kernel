using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Kernel;
using Missili.OneBot.Mock;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>独立 OneBot 与模型协议对端贯穿正式入口、真实工具和批次记录。</summary>
public sealed class AgentWorkflowTests
{
    /// <summary>两个子代理同时工作、兄弟通信、部分失败和明确 follow-up 均通过正式 OneBot 入口驱动。</summary>
    [Fact]
    public async Task ParallelChildrenCommunicateAndRecoverPartialFailure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource communicationSaved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCalls = 0, secondCalls = 0, started = 0, rootCalls = 0, followed = 0;
        Guid secondId = Guid.Empty;
        bool recovered = false, supplementSeen = false;
        await using TestHttpServer model = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement request = document.RootElement;
            string candidate = request.GetProperty("model").GetString()!;
            if (candidate is "B" or "C")
            {
                int call = candidate == "B" ? Interlocked.Increment(ref firstCalls) : Interlocked.Increment(ref secondCalls);
                if (call == 1)
                {
                    if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
                    await release.Task.WaitAsync(context.RequestAborted);
                    await AgentCommunicationTests.RespondAsync(context, null, [("list_agents", new())]);
                }
                else if (candidate == "B" && call == 2)
                {
                    JsonElement[] results = ToolResults(request);
                    secondId = results.Last().GetProperty("data").GetProperty("agents").EnumerateArray()
                        .Single(agent => agent.GetProperty("template").GetString() == "second").GetProperty("agent_id").GetGuid();
                    await AgentCommunicationTests.RespondAsync(context, null, [("send_agent_message", new() { ["agent_id"] = secondId, ["message"] = "兄弟提供的事实" })]);
                }
                else if (candidate == "C" && call == 2)
                {
                    await communicationSaved.Task.WaitAsync(context.RequestAborted);
                    await AgentCommunicationTests.RespondAsync(context, null, [("list_agents", new())]);
                }
                else if (candidate == "C" && call == 3)
                {
                    Assert.Contains("兄弟提供的事实", VisibleText(request));
                    context.Response.ContentType = "text/event-stream";
                    await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(context.Response,
                        "data: {\"id\":\"partial\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"C\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"已获得的部分成果\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n", context.RequestAborted);
                }
                else
                {
                    if (candidate == "B") communicationSaved.TrySetResult();
                    if (candidate == "C")
                    {
                        string inputs = VisibleText(request);
                        Assert.Contains("兄弟提供的事实", inputs); Assert.Contains("补完失败部分", inputs);
                    }
                    await AgentCommunicationTests.RespondAsync(context, candidate == "B" ? "第一份完整成果" : "第二份补完成果", []);
                }
                return;
            }
            int rootTurn = Interlocked.Increment(ref rootCalls);
            if (rootTurn == 1)
            {
                await AgentCommunicationTests.RespondAsync(context, null,
                    [("spawn_agent", new() { ["task"] = "并行第一份", ["template"] = "first" }),
                     ("spawn_agent", new() { ["task"] = "并行第二份", ["template"] = "second" }), ("finish_turn", new())]);
                return;
            }
            string text = VisibleText(request);
            supplementSeen |= text.Contains("用户追加的约束", StringComparison.Ordinal);
            bool hasPartial = text.Contains("已获得的部分成果", StringComparison.Ordinal);
            recovered |= text.Contains("第二份补完成果", StringComparison.Ordinal);
            if (recovered) await AgentCommunicationTests.RespondAsync(context, "两份成果已汇总", []);
            else if (hasPartial && Interlocked.CompareExchange(ref followed, 1, 0) == 0)
                await AgentCommunicationTests.RespondAsync(context, null, [("followup_agent", new() { ["agent_id"] = secondId, ["task"] = "补完失败部分，保留已知事实" }), ("finish_turn", new())]);
            else await AgentCommunicationTests.RespondAsync(context, null, [("finish_turn", new())]);
        }, token);
        string variable = "MISSILI_CHILD_SCENE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile
            {
                Enabled = true, Models = ["A"], Tools = [AgentToolIds.Spawn, AgentToolIds.Followup, AgentToolIds.FinishTurn], DefaultTemplate = "first",
                DelegatedTools = [AgentToolIds.List, AgentToolIds.Message],
                Templates = [new SubagentTemplate { Id = "first", Description = "第一份资料", Instructions = "传递事实", Models = ["B"], Tools = [AgentToolIds.List, AgentToolIds.Message] },
                    new SubagentTemplate { Id = "second", Description = "第二份资料", Instructions = "读取通信并保留部分结果", Models = ["C"], Tools = [AgentToolIds.List] }]
            } }]
            };
            await using OneBotFixture fixture = await OpenAsync(configuration, token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "/agent 并行处理两份资料"), token: token);
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            AgentTaskStatus active = (await fixture.Runtime.Agents.GetActiveAsync(OneBotFixture.Address(), token))!;
            Assert.Equal(2, active.ActiveWork);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(2, "/agent 用户追加的约束"), token: token);
            MessageSnapshot added = await fixture.WaitMessageAsync("2", token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (await ((IAgentStore)fixture.Runtime.History).FindWakeAsync(added.Id, deadline.Token) is null) await Task.Delay(10, deadline.Token);
            release.TrySetResult();
            await fixture.WaitActionAsync("send_group_msg", token);
            AgentTaskSnapshot task = await TaskFromCallsAsync(fixture, token);
            Assert.Equal(AgentTaskState.Completed, task.State); Assert.Equal(3, task.Usage.TotalAgents);
            Assert.Equal(3, firstCalls); Assert.Equal(4, secondCalls); Assert.Equal(1, followed);
            Assert.True(recovered); Assert.True(supplementSeen);
            Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == "send_group_msg");
        }
        finally { release.TrySetResult(); communicationSaved.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>多配置档按需使用回复 DLL，默认提示和流式生效，另一 DLL 分段后审批并逐条确认。</summary>
    [Fact]
    public async Task ToolsOnlyReplyProcessingApprovalAndReceipts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps =
        [Tool("reply", new() { ["instructions"] = "表达已完成事实", ["facts"] = new[] { "原词第一句。第二句。" } }),
            new ModelMockStep { Model = "B", Text = "原词第一句。第二句。" },
            new ModelMockStep { Model = "C", Text = "{\"decision\":\"allow\",\"reason\":\"最终内容符合明确要求\"}" }, Tool("finish_turn", new())]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_REPLY_SCENE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            using JsonDocument reply = JsonDocument.Parse("""{"models":["B"]}""");
            using JsonDocument process = JsonDocument.Parse("""{"steps":[{"type":"replace","pattern":"原词","replacement":"新词"},{"type":"split","maximumTextElements":5}]}""");
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with
            {
                Agent = configuration.Profiles[0].Agent with { OutputMode = AgentOutputMode.ToolsOnly, Tools = ["reply.generate", AgentToolIds.FinishTurn] },
                Outgoing = new OutgoingOptions { Processors = [new HandlerKey { PluginId = "missili.plugins.reply-processing", HandlerId = "process" }] },
                Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add("missili.tools.reply", reply.RootElement.Clone()).Add("missili.plugins.reply-processing", process.RootElement.Clone()),
                Approvals = new ToolApprovalOptions { Models = ["C"], Tools = ImmutableDictionary<string, ToolApprovalOverride>.Empty.Add(ApprovalActionIds.MessageSend, new() { Enabled = true }) }
            }, new ConversationProfile { Id = "without-reply" }, new ConversationProfile
            {
                Id = "empty-reply",
                Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add("missili.tools.reply", JsonSerializer.SerializeToElement(new Dictionary<string, object>()))
            }]
            };
            await using OneBotFixture fixture = await OpenAsync(configuration, token);
            await LoadAsync(fixture, "reply", token); await LoadAsync(fixture, "reply-processing", token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "/agent 请表达事实"), token: token);
            await fixture.WaitActionAsync("send_group_msg", token); await fixture.WaitActionAsync("send_group_msg", token);
            AgentTaskSnapshot task = await TaskFromCallsAsync(fixture, token);
            Assert.Equal(4, task.Usage.ModelCalls); Assert.Equal(2, task.Usage.ToolCalls);
            BatchSendResult batch = Assert.Single(await ((IOutgoingHistory)fixture.Runtime.History).GetTaskBatchesAsync(OneBotFixture.Address(), task.Id, 20, token));
            Assert.True(batch.Confirmed); Assert.Equal(2, batch.Messages.Length);
            OutgoingBatchSnapshot audit = (await ((IOutgoingHistory)fixture.Runtime.History).GetBatchAsync(OneBotFixture.Address(), batch.BatchId, token))!;
            Assert.Equal(new[] { "新词第一句", "。第二句。" }, audit.FinalMessages.Select(draft => draft.Segments[0].Text));
            Assert.Equal(ToolApprovalState.Allowed, audit.Approval!.State);
            Assert.Equal(new[] { "A", "B", "C", "A" }, model.Requests.Select(request => request.Model));
            JsonElement replyRequest = model.Requests[1].Body!.Value;
            Assert.True(replyRequest.GetProperty("stream").GetBoolean());
            Assert.Contains("依据本次要求", VisibleText(replyRequest));
            Assert.DoesNotContain("Hikarier.Missili.Kittens", VisibleText(replyRequest));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>实际 shell 首次因授权不足零执行，询问后新明确命令才获准执行一次。</summary>
    [Fact]
    public async Task DeniedShellClarifiesThenExecutesNewApprovedCallOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int decisions = 0, reviews = 0;
        string workingDirectory = "", marker = "";
        await using TestHttpServer model = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.GetProperty("model").GetString() == "B")
            {
                bool allow = Interlocked.Increment(ref reviews) == 2;
                await AgentCommunicationTests.RespondAsync(context, allow ? "{\"decision\":\"allow\",\"reason\":\"用户已明确授权此合成动作\"}" : "{\"decision\":\"deny\",\"reason\":\"授权不足，需要相关用户明确同意写入标记\"}", []);
                return;
            }
            int turn = Interlocked.Increment(ref decisions);
            if (turn is 1 or 3)
            {
                string command = OperatingSystem.IsWindows() ? "[IO.File]::AppendAllText('" + marker.Replace("'", "''", StringComparison.Ordinal) + "','x')"
                    : "printf x >> '" + marker.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
                await AgentCommunicationTests.RespondAsync(context, null, [("shell_execute", new() { ["command"] = command, ["working_directory"] = workingDirectory })]);
            }
            else await AgentCommunicationTests.RespondAsync(context, turn == 2 ? "请明确授权写入合成标记。" : "已执行一次授权命令。", []);
        }, token);
        string variable = "MISSILI_CLARIFY_SCENE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with
            {
                Agent = configuration.Profiles[0].Agent with { Tools = ["shell.execute"] },
                Approvals = new ToolApprovalOptions { Models = ["B"], Tools = ImmutableDictionary<string, ToolApprovalOverride>.Empty.Add("shell.execute", new() { Enabled = true }) }
            }]
            };
            await using OneBotFixture fixture = await OpenAsync(configuration, token);
            workingDirectory = fixture.Root; marker = Path.Combine(fixture.Root, "approved-once.txt");
            Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("shell"), JsonSerializer.SerializeToElement(ShellToolTests.Settings()), token)).State);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "/agent 处理合成任务"), token: token);
            await fixture.WaitActionAsync("send_group_msg", token);
            AgentTaskSnapshot denied = await TaskFromCallsAsync(fixture, token);
            Assert.False(File.Exists(marker)); Assert.Equal(1, reviews);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(2, "/agent 明确允许在本任务临时目录写入合成标记"), token: token);
            await fixture.WaitActionAsync("send_group_msg", token);
            AgentTaskSnapshot approved = await TaskFromCallsAsync(fixture, token);
            Assert.NotEqual(denied.Id, approved.Id);
            Assert.Equal("x", await File.ReadAllTextAsync(marker, token));
            Assert.Equal(2, reviews); Assert.Equal(4, decisions);
            AgentEntryPage entries = await ((IAgentHistory)fixture.Runtime.History).ReadEntriesAsync(OneBotFixture.Address(), approved.Id, approved.RootAgentId, 0, 100, token);
            Assert.Single(entries.Entries, entry => entry.Kind == AgentEntryKind.ToolResults);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>入口登记运行中唤醒，管理员停止只保留成果，整理不会再次公开回复。</summary>
    [Fact]
    public async Task OneBotStopKeepsPartialWorkAndOnlyOneSettlement()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps =
            [new ModelMockStep { WaitForSignal = "complete-work", Text = "停止前已完成部分" }, new ModelMockStep { Text = "已完成部分；其余尚未处理。" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_STOP_SCENE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            configuration = configuration with { Profiles = [configuration.Profiles[0] with { Agent = configuration.Profiles[0].Agent with { Administrators = ["30001"] } }] };
            await using OneBotFixture fixture = await OpenAsync(configuration, token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "/agent 开始持续工作"), token: token);
            await model.NextRequestAsync(token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(2, "/agent 再补充一个要求"), token: token);
            MessageSnapshot added = await fixture.WaitMessageAsync("2", token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await ((IAgentStore)fixture.Runtime.History).FindWakeAsync(added.Id, deadline.Token)) is null) await Task.Delay(10, deadline.Token);
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(3, "/agent stop"), token: token);
            await fixture.WaitActionAsync("send_group_msg", token);
            model.Release("complete-work");
            AgentTaskSnapshot task = await TaskFromCallsAsync(fixture, token);
            Assert.Equal(AgentTaskState.Cancelled, task.State); Assert.Equal(2, model.TotalRequests);
            Assert.Empty(await ((IOutgoingHistory)fixture.Runtime.History).GetTaskBatchesAsync(OneBotFixture.Address(), task.Id, 20, token));
            AgentSnapshot root = Assert.Single(await ((IAgentHistory)fixture.Runtime.History).GetAgentsAsync(OneBotFixture.Address(), task.Id, token));
            Assert.Contains("已完成部分", root.Result!.Content[0].Text);
            Assert.False(root.Result.Complete);
            Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == "send_group_msg");
        }
        finally { model.Release("complete-work"); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>第一条确认、第二条回执丢失时第三条不发；迟到确认和重启不补发业务。</summary>
    [Fact]
    public async Task UnknownBatchLateReceiptAndRestartDoNotReplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = [new ModelMockStep { Text = "甲乙丙" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_UNKNOWN_SCENE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(model.BaseAddress, variable);
            using JsonDocument process = JsonDocument.Parse("""{"steps":[{"type":"split","maximumTextElements":1}]}""");
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with
            {
                Outgoing = new OutgoingOptions { Processors = [new HandlerKey { PluginId = "missili.plugins.reply-processing", HandlerId = "process" }] },
                Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add("missili.plugins.reply-processing", process.RootElement.Clone())
            }]
            };
            await using OneBotFixture fixture = await OpenAsync(configuration, token, clock);
            await LoadAsync(fixture, "reply-processing", token);
            fixture.Peer.AutoRespond = false;
            await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "/agent 回复三段"), token: token);
            MockAction first = await fixture.WaitActionAsync("send_group_msg", token);
            await first.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["message_id"] = 401 }), token);
            MockAction second = await fixture.WaitActionAsync("send_group_msg", token);
            clock.Advance(TimeSpan.FromSeconds(31));
            AgentTaskSnapshot task = await TaskFromCallsAsync(fixture, token);
            IOutgoingHistory outgoing = (IOutgoingHistory)fixture.Runtime.History;
            BatchSendResult unknown = Assert.Single(await outgoing.GetTaskBatchesAsync(OneBotFixture.Address(), task.Id, 20, token));
            Assert.Equal(BatchSendState.Unconfirmed, unknown.State); Assert.Equal(2, unknown.Messages.Length);
            await second.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["message_id"] = 402 }), token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await outgoing.GetBatchAsync(OneBotFixture.Address(), unknown.BatchId, deadline.Token))!.Result.State != BatchSendState.PartiallySent) await Task.Delay(10, deadline.Token);
            await fixture.Runtime.StopAsync(token);
            await using InfrastructureRuntime restarted = Missili.Host.InfrastructureFactory.Create(new InfrastructureOptions
            { DataDirectory = fixture.Root, ConversationConfigurationDirectory = Path.Combine(fixture.Root, "model-config") }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                modelOptions: new ModelOptions { Enabled = true }, agentOptions: new AgentOptions { Enabled = true });
            await restarted.StartAsync(token); restarted.OpenAgents();
            Assert.Null(await restarted.Agents.GetActiveAsync(OneBotFixture.Address(), token));
            OutgoingBatchSnapshot saved = (await ((IOutgoingHistory)restarted.History).GetBatchAsync(OneBotFixture.Address(), unknown.BatchId, token))!;
            Assert.Equal(BatchSendState.PartiallySent, saved.Result.State); Assert.Equal(2, saved.Result.UnattemptedFrom);
            Assert.Equal(1, model.TotalRequests);
            Assert.Equal(2, fixture.Peer.RecordedActions.Count(action => action.GetProperty("action").GetString() == "send_group_msg"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>完整工具响应独立按协议编写，不用生产编码器作为预期生成器。</summary>
    private static ModelMockStep Tool(string name, Dictionary<string, object> arguments)
    {
        string response = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "scene",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "A",
            ["choices"] = new[] { new Dictionary<string, object> { ["index"] = 0, ["finish_reason"] = "tool_calls", ["delta"] = new Dictionary<string, object>
            { ["tool_calls"] = new[] { new Dictionary<string, object> { ["index"] = 0, ["id"] = Guid.NewGuid().ToString("N"), ["type"] = "function",
                ["function"] = new Dictionary<string, object> { ["name"] = name, ["arguments"] = JsonSerializer.Serialize(arguments) } } } } } }
        });
        return new ModelMockStep { Model = "A", Events = ["data: " + response + "\n\n", "data: [DONE]\n\n"] };
    }

    /// <summary>提取真实协议工具结果，用于引用宿主生成的代理身份。</summary>
    private static JsonElement[] ToolResults(JsonElement request) => request.GetProperty("messages").EnumerateArray()
        .Where(message => message.GetProperty("role").GetString() == "tool").Select(message =>
        { using JsonDocument document = JsonDocument.Parse(MessageText(message)); return document.RootElement.Clone(); }).ToArray();

    /// <summary>展开通信 JSON 的正文用于场景条件，不假定 JSON 序列化是否转义中文。</summary>
    private static string VisibleText(JsonElement request) => string.Join("\n", request.GetProperty("messages").EnumerateArray().Select(message =>
    {
        string text = MessageText(message);
        if (!text.StartsWith('{')) return text;
        using JsonDocument document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }));

    /// <summary>兼容协议定义的字符串和文本块，不读取原生推理。</summary>
    private static string MessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null) return "";
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
    }

    /// <summary>三个用途各有候选名，模型连接及合成凭据仍使用一个目录。</summary>
    private static ConversationConfiguration Configuration(Uri address, string variable)
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address, variable, ModelProtocol.ChatCompletions);
        return configuration with
        {
            Models = configuration.Models.Select(model => model with { Tools = true }).Append(configuration.Models[1] with { Id = "C", Name = "C", Tools = true }).ToImmutableArray(),
            Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile { Enabled = true, Models = ["A"] } }]
        };
    }

    /// <summary>通过生产宿主、真实适配器和官方入口接单，不直接调用 Agent 提交方法。</summary>
    private static async Task<OneBotFixture> OpenAsync(ConversationConfiguration configuration, CancellationToken token, TimeProvider? clock = null)
    {
        OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token, models: configuration, enableAgents: true, clock: clock);
        await LoadAsync(fixture, "openai", token); await LoadAsync(fixture, "agent-chat", token); return fixture;
    }

    /// <summary>各官方能力来自实际构建目录，宿主默认程序集上下文不引用实现。</summary>
    private static async Task LoadAsync(OneBotFixture fixture, string name, CancellationToken token)
        => Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath(name), RuntimeFixture.Configuration(), token)).State);

    /// <summary>从已归属的模型记录取得最新任务，等待真实资源释放后读取结果。</summary>
    private static async Task<AgentTaskSnapshot> TaskFromCallsAsync(OneBotFixture fixture, CancellationToken token)
    {
        ModelCallPage calls = await fixture.Runtime.Models.Calls.QueryCallsAsync(new ModelCallQuery { Purpose = "agent", Conversation = OneBotFixture.Address() }, token);
        Guid taskId = calls.Calls[0].Origin!.TaskId!.Value;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            AgentTaskSnapshot task = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(OneBotFixture.Address(), taskId, deadline.Token))!;
            if (task.ReleasedAt.HasValue) return task;
            await Task.Delay(10, deadline.Token);
        }
    }
}
