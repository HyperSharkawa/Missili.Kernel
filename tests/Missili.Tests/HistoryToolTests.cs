using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Nexus;
using Xunit;

namespace Missili.Tests;

/// <summary>通过实际 Agent 循环使用历史插件，范围、可见性和大结果分页由生产路径执行。</summary>
public sealed class HistoryToolTests
{
    /// <summary>历史可读取旧任务，撤回原文和原生续接不能从不同工具入口漏出。</summary>
    [Fact]
    public async Task HistoricalReadsApplyVisibilityAndExcludeNativeDiagnostics()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid previousTask = Guid.NewGuid(), previousAgent = Guid.NewGuid();
        ConcurrentQueue<JsonElement> requests = new();
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            requests.Enqueue(document.RootElement.Clone());
            if (Interlocked.Increment(ref calls) == 1) await AgentCommunicationTests.RespondAsync(context, null,
            [
                ("search_history", new Dictionary<string, object> { ["query"] = "secret" }),
                ("read_history", new Dictionary<string, object> { ["page_size"] = 20 }),
                ("read_task_record", new Dictionary<string, object> { ["task_id"] = previousTask, ["agent_id"] = previousAgent, ["page_size"] = 100 })
            ]);
            else await AgentCommunicationTests.RespondAsync(context, "历史读取完成", []);
        }, token);
        string variable = "MISSILI_HISTORY_TOOLS_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await CreateAsync(server.BaseAddress, variable, token);
            IAgentStore store = (IAgentStore)fixture.Runtime.History;
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message("secret 原始正文") with
            { Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["raw"] = "secret 元数据正文" }) }, token)).Message!;
            await AdmitPreviousAsync(fixture, source, previousTask, previousAgent, token);
            ModelResult model = new()
            {
                CallId = Guid.NewGuid(),
                State = ModelCallState.Succeeded,
                Complete = true,
                Content = [ModelBlock.TextBlock("公开模型结果"), new ModelBlock { Kind = ModelBlockKind.Native, Text = "secret thinking" }],
                Continuation = new ModelContinuation
                {
                    Protocol = ModelProtocol.Responses,
                    ProviderId = "test",
                    ModelId = "A",
                    ModelName = "A",
                    Endpoint = server.BaseAddress,
                    Items = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["signature"] = "secret signature" })
                }
            };
            await store.AppendEntryAsync(previousAgent, AgentEntryKind.ModelOutput, JsonSerializer.SerializeToElement(model), TestStore.Now, token);
            await store.CompleteTurnAsync(previousAgent, new AgentResult { Complete = true, Content = [ModelBlock.TextBlock("公开历史成果"), new ModelBlock { Kind = ModelBlockKind.Native, Text = "secret native result" }] }, TestStore.Now, true, token);
            await store.FinishTaskAsync(previousTask, AgentTaskState.Completed, null, TestStore.Now, true, token);
            await ((IInfrastructureStore)store).SetVisibilityAsync(source.Id, true, false, token);
            fixture.Runtime.OpenAgents();
            AgentWakeResult active = await WakeAsync(fixture, "读取旧记录", token);
            await FinishAsync(fixture, active.TaskId!.Value, token);
            JsonElement[] outputs = ToolOutputs(requests.Last()).ToArray();
            Assert.Equal(3, outputs.Length);
            Assert.All(outputs, output => { Assert.Equal("Succeeded", output.GetProperty("state").GetString()); Assert.DoesNotContain("secret", output.GetRawText(), StringComparison.OrdinalIgnoreCase); });
            Assert.Empty(outputs[0].GetProperty("data").GetProperty("items").EnumerateArray());
            Assert.Contains(outputs[1].GetProperty("data").GetProperty("items").EnumerateArray(), message => message.GetProperty("id").GetInt64() == source.Id && message.GetProperty("metadata").ValueKind == JsonValueKind.Null);
            JsonElement nativeRecord = outputs[2].GetProperty("data").GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("kind").GetString() == "model_output").GetProperty("data");
            Assert.Equal(JsonValueKind.Null, nativeRecord.GetProperty("Continuation").ValueKind);
            Assert.Single(nativeRecord.GetProperty("Content").EnumerateArray());
            string package = OfficialPluginPackages.GetPath("history");
            Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("missili.tools.history", package, RuntimeFixture.Configuration(), token)).State);
            WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("missili.tools.history")!;
            for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
            Assert.False(reference.IsAlive);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>较大成果可多次展开重建，模型结果不超限，不重新执行旧任务或工具。</summary>
    [Fact]
    public async Task LargeHistoricalResultCanBeReadCompletelyThroughBoundedChunks()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid entryId = Guid.Empty;
        StringBuilder restored = new();
        int reads = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            JsonElement[] outputs = ToolOutputs(document.RootElement).ToArray();
            int offset = 0;
            if (outputs.Length > 0)
            {
                JsonElement latest = outputs[^1].GetProperty("data");
                restored.Append(latest.GetProperty("content").GetString());
                Assert.True(Encoding.UTF8.GetByteCount(outputs[^1].GetRawText()) <= 64 * 1024);
                if (latest.GetProperty("next_offset").ValueKind == JsonValueKind.Null) { await AgentCommunicationTests.RespondAsync(context, "记录已完整读取", []); return; }
                offset = latest.GetProperty("next_offset").GetInt32();
            }
            Interlocked.Increment(ref reads);
            await AgentCommunicationTests.RespondAsync(context, null, [("read_task_record", new Dictionary<string, object> { ["entry_id"] = entryId, ["offset"] = offset, ["length"] = 8192 })]);
        }, token);
        string variable = "MISSILI_HISTORY_PAGES_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await CreateAsync(server.BaseAddress, variable, token);
            IAgentStore store = (IAgentStore)fixture.Runtime.History;
            MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message("旧任务来源"), token)).Message!;
            Guid previousTask = Guid.NewGuid(), previousAgent = Guid.NewGuid();
            await AdmitPreviousAsync(fixture, message, previousTask, previousAgent, token);
            string full = string.Concat(Enumerable.Repeat("较大的中文成果👩🏽‍💻", 1000));
            AgentEntry entry = await store.AppendEntryAsync(previousAgent, AgentEntryKind.Result, JsonSerializer.SerializeToElement(new AgentResult { Complete = true, Content = [ModelBlock.TextBlock(full)] }), TestStore.Now, token);
            entryId = entry.Id;
            await store.FinishTaskAsync(previousTask, AgentTaskState.Completed, null, TestStore.Now, true, token);
            fixture.Runtime.OpenAgents();
            AgentWakeResult active = await WakeAsync(fixture, "逐页读取以前的完整成果", token);
            await FinishAsync(fixture, active.TaskId!.Value, token);
            Assert.InRange(reads, 2, 30);
            using JsonDocument reconstructed = JsonDocument.Parse(restored.ToString());
            Assert.Equal(full, reconstructed.RootElement.GetProperty("data").GetProperty("Content")[0].GetProperty("Text").GetString());
            Assert.Single(fixture.Transport.Requests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>实际 SDK 发送的工具结果作为独立 JSON 数据读取，不依赖生产编码器。</summary>
    private static IEnumerable<JsonElement> ToolOutputs(JsonElement request)
    {
        foreach (JsonElement message in request.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() != "tool") continue;
            JsonElement content = message.GetProperty("content");
            string text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            using JsonDocument document = JsonDocument.Parse(text); yield return document.RootElement.Clone();
        }
    }

    /// <summary>配置当前会话的工具白名单，提供商与工具都从实际 DLL 加载。</summary>
    private static async Task<RuntimeFixture> CreateAsync(Uri address, string variable, CancellationToken token)
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address, variable, ModelProtocol.ChatCompletions);
        configuration = configuration with
        {
            Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
            Profiles = [configuration.Profiles[0] with
        { Visibility = MessageVisibilityPolicy.Placeholder, Agent = new AgentProfile { Enabled = true, Models = ["A"], Tools = ["history.search", "history.read", "history.read_task_record"] } }]
        };
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
        await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("history"), RuntimeFixture.Configuration(), token)).State);
        return fixture;
    }

    /// <summary>创建已经结束的真实存储任务，测试新任务不会自动继承其工具轨迹。</summary>
    private static Task<AgentWakeResult> AdmitPreviousAsync(RuntimeFixture fixture, MessageSnapshot source, Guid task, Guid agent, CancellationToken token)
        => ((IAgentStore)fixture.Runtime.History).RegisterWakeAsync(new AgentWakeAdmission
        {
            WakeRequest = new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = source.Id, CurrentContent = source.Segments },
            Profile = fixture.Runtime.Profiles.Resolve(source.Address),
            RunId = Guid.NewGuid(),
            NewTaskId = task,
            NewRootAgentId = agent,
            Now = TestStore.Now
        }, token);

    /// <summary>当前任务通过生产入口服务接受已保存的明确用户要求。</summary>
    private static async Task<AgentWakeResult> WakeAsync(RuntimeFixture fixture, string text, CancellationToken token)
    {
        MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message(text), token)).Message!;
        AgentWakeResult admitted = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = source.Id, CurrentContent = source.Segments }, token);
        Assert.True(admitted.Registered, admitted.Error); return admitted;
    }

    /// <summary>以真实释放记录作为完成条件，读取历史不会启动旧任务。</summary>
    private static async Task FinishAsync(RuntimeFixture fixture, Guid task, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            AgentTaskSnapshot status = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(TestStore.Address(), task, deadline.Token))!;
            if (status.ReleasedAt.HasValue) { Assert.Equal(AgentTaskState.Completed, status.State); return; }
            await Task.Delay(10, deadline.Token);
        }
    }
}
