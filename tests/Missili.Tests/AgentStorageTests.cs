using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Missili.Abstractions;
using Missili.Persistence;
using Xunit;

namespace Missili.Tests;

/// <summary>用真实 SQLite 验证唯一主代理、输入归属、预算与不会重放业务的中断记录。</summary>
public sealed class AgentStorageTests
{
    /// <summary>多成员并发输入归入同一主代理，重复消息不产生第二次接单。</summary>
    [Fact]
    public async Task ConcurrentInputsKeepOneRootAndOriginalSenders()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        List<MessageSnapshot> messages = [];
        for (int index = 0; index < 20; index++) messages.Add((await fixture.Store.AppendAsync(
            TestStore.Message("需求 " + index) with { SenderId = "member-" + index }, TestStore.Now, fixture.RunId, token)).Message!);
        ConversationProfileSnapshot profile = Profile();
        AgentWakeResult[] accepted = await Task.WhenAll(messages.Select(message => fixture.Store.RegisterWakeAsync(Request(fixture, message, profile), token)));
        Assert.Single(accepted, item => item.State == AgentWakeState.Created);
        Assert.Equal(19, accepted.Count(item => item.State == AgentWakeState.Queued));
        Guid taskId = Assert.Single(accepted.Select(item => item.TaskId!.Value).Distinct());
        Guid agentId = Assert.Single(accepted.Select(item => item.AgentId!.Value).Distinct());
        AgentWakeResult duplicate = await fixture.Store.RegisterWakeAsync(Request(fixture, messages[0], profile), token);
        Assert.Equal(AgentWakeState.Duplicate, duplicate.State);
        Assert.Equal(accepted[0].EntrySequence, duplicate.EntrySequence);
        AgentEntryPage first = await fixture.Store.ReadEntriesAsync(TestStore.Address(), taskId, agentId, 0, 7, token);
        AgentEntryPage second = await fixture.Store.ReadEntriesAsync(TestStore.Address(), taskId, agentId, first.NextSequence!.Value, 100, token);
        AgentEntry[] entries = [.. first.Entries, .. second.Entries];
        Assert.Equal(20, entries.Length);
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), entries.Select(entry => entry.Sequence));
        Assert.Equal(20, entries.Select(entry => entry.Data.GetProperty("SenderId").GetString()).Distinct().Count());
        Assert.Null(await fixture.Store.GetTaskAsync(TestStore.Address("another"), taskId, token));
        AgentTaskSnapshot task = (await fixture.Store.GetActiveTaskAsync(TestStore.Address(), token))!;
        Assert.Equal(taskId, task.Id);
        Assert.Equal(messages[0].SenderId, task.TriggerSenderId);
    }

    /// <summary>终态但尚未实际释放仍占有会话，新消息不能抢走该归属。</summary>
    [Fact]
    public async Task FinalStateDoesNotReleaseConversationBeforeResourcesExit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot original = (await fixture.Store.AppendAsync(TestStore.Message("第一次"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult admitted = await fixture.Store.RegisterWakeAsync(Request(fixture, original, Profile()), token);
        await fixture.Store.FinishTaskAsync(admitted.TaskId!.Value, AgentTaskState.Failed, "合成资源尚未退出", TestStore.Now, false, token);
        MessageSnapshot next = (await fixture.Store.AppendAsync(TestStore.Message("新任务"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeAdmission request = Request(fixture, next, Profile());
        Assert.Equal(AgentWakeState.Busy, (await fixture.Store.RegisterWakeAsync(request, token)).State);
        await fixture.Store.FinishTaskAsync(admitted.TaskId.Value, AgentTaskState.Failed, "合成资源已退出", TestStore.Now, true, token);
        AgentWakeResult created = await fixture.Store.RegisterWakeAsync(request, token);
        Assert.Equal(AgentWakeState.Created, created.State);
        Assert.NotEqual(admitted.TaskId, created.TaskId);
        Assert.Equal("第一次", (await fixture.Store.GetAsync(original.Id, token))!.Segments[0].Text);
    }

    /// <summary>调用和创建并发竞争同一预算，停止子代理不返还累计创建次数。</summary>
    [Fact]
    public async Task CallsAndChildCreationReserveSharedBudgetsAtomically()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("预算任务"), TestStore.Now, fixture.RunId, token)).Message!;
        ConversationProfileSnapshot profile = Profile(new AgentBudget { DecisionsPerAgent = 2, ModelCalls = 2, ToolCalls = 3, TotalAgents = 3, WorkingAgents = 2, MaximumDepth = 1 });
        AgentWakeResult root = await fixture.Store.RegisterWakeAsync(Request(fixture, message, profile), token);
        bool[] models = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Store.ReserveCallsAsync(root.AgentId!.Value, 1, 0, true, false, token)));
        bool[] tools = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.Store.ReserveCallsAsync(root.AgentId!.Value, 0, 1, false, false, token)));
        Assert.Equal(2, models.Count(value => value));
        Assert.Equal(3, tools.Count(value => value));
        JsonElement input = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["task"] = "合成子任务" });
        AgentSnapshot?[] children = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Store.CreateChildAsync(root.AgentId!.Value, "worker", "合成子任务", input, TestStore.Now, token)));
        AgentSnapshot[] created = children.OfType<AgentSnapshot>().ToArray();
        Assert.Equal(2, created.Length);
        Assert.Null(await fixture.Store.CreateChildAsync(created[0].Id, "worker", "不能超过深度", input, TestStore.Now, token));
        await fixture.Store.CompleteTurnAsync(created[0].Id, new AgentResult { Complete = false, Error = "已停止" }, TestStore.Now, true, token);
        Assert.Null(await fixture.Store.CreateChildAsync(root.AgentId!.Value, "worker", "不能返还额度", input, TestStore.Now, token));
        AgentUsage usage = (await fixture.Store.GetTaskAsync(TestStore.Address(), root.TaskId!.Value, token))!.Usage;
        Assert.Equal(2, usage.ModelCalls); Assert.Equal(3, usage.ToolCalls); Assert.Equal(3, usage.TotalAgents);
    }

    /// <summary>树级停止共享原截止时间，重复对子节点停止不能新增整理机会。</summary>
    [Fact]
    public async Task StopDeadlineAndSettlementAreSharedAndCannotBeReset()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("需要停止"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult root = await fixture.Store.RegisterWakeAsync(Request(fixture, message, Profile()), token);
        JsonElement input = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["task"] = "子任务" });
        AgentSnapshot child = (await fixture.Store.CreateChildAsync(root.AgentId!.Value, "worker", "子任务", input, TestStore.Now, token))!;
        DateTimeOffset deadline = TestStore.Now.AddSeconds(180);
        await fixture.Store.RequestStopAsync(root.TaskId!.Value, root.AgentId.Value, [root.AgentId.Value, child.Id], TestStore.Now, deadline, token);
        await fixture.Store.RequestStopAsync(root.TaskId.Value, child.Id, [child.Id], TestStore.Now.AddSeconds(20), deadline.AddSeconds(20), token);
        ImmutableArray<AgentSnapshot> agents = await fixture.Store.GetAgentsAsync(TestStore.Address(), root.TaskId.Value, token);
        Assert.All(agents, agent => Assert.Equal(deadline, agent.StopDeadline));
        Assert.False(await fixture.Store.ReserveCallsAsync(child.Id, 1, 0, false, true, token));
        Assert.True(await fixture.Store.ReserveCallsAsync(root.AgentId.Value, 1, 0, false, true, token));
        Assert.False(await fixture.Store.ReserveCallsAsync(root.AgentId.Value, 1, 0, false, true, token));
        Assert.False(await fixture.Store.ReserveCallsAsync(root.AgentId.Value, 0, 1, false, false, token));
    }

    /// <summary>重启保留部分成果与原输入，只释放中断的归属，不创建可重放工作。</summary>
    [Fact]
    public async Task RestartPreservesPartialResultsAndClosesInterruptedTasks()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("中断任务"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult root = await fixture.Store.RegisterWakeAsync(Request(fixture, message, Profile()), token);
        await fixture.Store.CompleteTurnAsync(root.AgentId!.Value,
            new AgentResult { Content = [ModelBlock.TextBlock("已经取得的部分成果")], Complete = false }, TestStore.Now, false, token);
        await fixture.Store.InitializeAsync(Guid.NewGuid(), token);
        AgentTaskSnapshot task = (await fixture.Store.GetTaskAsync(TestStore.Address(), root.TaskId!.Value, token))!;
        Assert.Equal(AgentTaskState.Interrupted, task.State);
        Assert.NotNull(task.ReleasedAt);
        Assert.Null(await fixture.Store.GetActiveTaskAsync(TestStore.Address(), token));
        AgentSnapshot agent = Assert.Single(await fixture.Store.GetAgentsAsync(TestStore.Address(), task.Id, token));
        Assert.Equal(AgentActivity.Closed, agent.Activity);
        Assert.Equal("已经取得的部分成果", agent.Result!.Content[0].Text);
        Assert.False(agent.Result.Complete);
        Assert.Equal(2, (await fixture.Store.ReadEntriesAsync(TestStore.Address(), task.Id, agent.Id, 0, 100, token)).Entries.Length);
    }

    /// <summary>第三阶段数据升级后保留消息和调用，新增字段具有明确的空历史含义。</summary>
    [Fact]
    public async Task UpgradePreservesOldMessagesAndModelCalls()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, ForeignKeys = true }.ToString()).Options);
        IMigrator migrations = database.GetService<IMigrator>();
        await migrations.MigrateAsync("20261104002700_ModelContextAndCalls", token);
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("升级前的聊天"), TestStore.Now, fixture.RunId, token)).Message!;
        Guid callId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ModelCalls(Id,RunId,Purpose,ProfileId,ConfigurationRevision,StartedAt,State) VALUES({callId},{fixture.RunId},{"old-call"},{"old-profile"},{1},{1000},{0})", token);
        await migrations.MigrateAsync(cancellationToken: token);
        ModelCallRecord call = (await fixture.Store.GetCallAsync(callId, token))!;
        Assert.Null(call.Origin); Assert.Empty(call.ExcludedCandidates);
        Assert.Equal("升级前的聊天", (await fixture.Store.GetAsync(message.Id, token))!.Segments[0].Text);
        Assert.False(database.Database.HasPendingModelChanges());
    }

    /// <summary>第四阶段已有根描述、子分工、输入、成果和模型关联在重命名迁移后完整保留。</summary>
    [Fact]
    public async Task UpgradeFromPhaseFourPreservesRuntimeSourcesAndSearch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot source = (await fixture.Store.AppendAsync(TestStore.Message("旧输入正文"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult wake = await fixture.Store.RegisterWakeAsync(Request(fixture, source, Profile()), token);
        Guid taskId = wake.TaskId!.Value, rootId = wake.AgentId!.Value;
        AgentSnapshot child = (await fixture.Store.CreateChildAsync(rootId, "worker", "旧子代理分工",
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["task"] = "旧子代理分工" }), TestStore.Now, token))!;
        await fixture.Store.CompleteTurnAsync(child.Id, new AgentResult { Complete = true, Content = [ModelBlock.TextBlock("旧子成果")] }, TestStore.Now, true, token);
        Guid callId = Guid.NewGuid();
        await fixture.Store.CreateCallAsync(new ModelCallRecord { Id = callId, RunId = fixture.RunId, Purpose = "migration", ProfileId = "profile",
            StartedAt = TestStore.Now, State = ModelCallState.Running, Origin = new OperationOrigin { TaskId = taskId, AgentId = rootId } }, token);
        await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, ForeignKeys = true }.ToString()).Options);
        IMigrator migrations = database.GetService<IMigrator>();
        AgentToolIntent intent = new() { Name = "legacy_tool", ToolCallId = "legacy-call", Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["value"] = "旧参数" }) };
        OperationOrigin origin = new() { TaskId = taskId, AgentId = rootId, ToolInvocationId = intent.Id };
        ToolApprovalResult approval = new() { State = ToolApprovalState.Allowed, Reason = "历史审批原结论" };
        ToolApprovalRequest review = new()
        {
            ToolId = "legacy.tool", Description = "旧动作", PermissionCategory = "test", Conversation = source.Address, Profile = Profile(), Origin = origin,
            Prepared = new PreparedToolCall { Arguments = intent.Arguments },
            Evidence = [new ToolApprovalEvidence { Kind = "task_initiator", SenderId = source.SenderId, Content = JsonSerializer.SerializeToElement("旧审批证据不可重新解释") }]
        };
        string approvalJson = JsonSerializer.Serialize(approval), reviewJson = JsonSerializer.Serialize(review);
        database.AgentToolCalls.Add(new AgentToolCallEntity
        {
            Id = intent.Id, TaskId = taskId, AgentId = rootId, ModelCallId = callId, TurnId = Guid.NewGuid(), ToolCallId = intent.ToolCallId,
            IntentJson = JsonSerializer.Serialize(intent), PreparedJson = JsonSerializer.Serialize(review.Prepared), ApprovalRequestJson = reviewJson,
            ApprovalJson = approvalJson, State = ToolCallState.Succeeded, CreatedAt = TestStore.Now.ToUnixTimeMilliseconds(), FinishedAt = TestStore.Now.ToUnixTimeMilliseconds()
        });
        await database.SaveChangesAsync(token);
        OutgoingBatch batch = OutgoingBatch.Single(source.Address, [SegmentValues.Text("旧批次正文")]) with { Origin = origin };
        await fixture.Store.CreateBatchAsync(batch, fixture.RunId, TestStore.Now, token);
        await fixture.Store.SetBatchContentAsync(batch.Id, batch.Messages, [], token);
        await fixture.Store.SetBatchApprovalAsync(batch.Id, review with { ToolId = ApprovalActionIds.MessageSend }, approval, token);
        MessageSnapshot sent = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage
        { Address = source.Address, Segments = batch.Messages[0].Segments, BatchId = batch.Id, BatchPosition = 0 }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.SetDeliveryAsync(sent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "legacy-sent" }, token);
        await fixture.Store.FinishBatchAsync(new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Completed, Recorded = true, TotalMessages = 1,
            Messages = [new SendResult { MessageId = sent.Id, State = DeliveryState.Confirmed, Recorded = true }] }, TestStore.Now, token);
        await migrations.MigrateAsync("20261105234134_AgentRuntimeAndOutgoing", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentSessions SET Task = {"旧主代理描述"} WHERE Id = {rootId}", token);
        await database.Database.ExecuteSqlRawAsync("UPDATE AgentEntries SET DataJson = json_remove(DataJson, '$.Cause') WHERE Kind = 0;", token);
        await migrations.MigrateAsync(cancellationToken: token);
        AgentTaskSnapshot task = (await fixture.Store.GetTaskAsync(source.Address, taskId, token))!;
        Assert.Equal(source.SenderId, task.TriggerSenderId);
        ImmutableArray<AgentSnapshot> agents = await fixture.Store.GetAgentsAsync(source.Address, taskId, token);
        Assert.Equal("旧主代理描述", agents.Single(agent => agent.Id == rootId).Assignment);
        Assert.Equal("旧子代理分工", agents.Single(agent => agent.Id == child.Id).Assignment);
        Assert.Equal("旧子成果", agents.Single(agent => agent.Id == child.Id).Result!.Content[0].Text);
        AgentWakeInput input = (await fixture.Store.ReadEntriesAsync(source.Address, taskId, rootId, 0, 1, token)).Entries[0].Data.Deserialize<AgentWakeInput>()!;
        Assert.Equal(AgentWakeKind.Unknown, input.Cause.Kind); Assert.Equal("旧输入正文", input.Content[0].Text);
        Assert.Equal(taskId, (await fixture.Store.GetCallAsync(callId, token))!.Origin!.TaskId);
        AgentToolCallEntity retained = await database.AgentToolCalls.AsNoTracking().SingleAsync(item => item.Id == intent.Id, token);
        Assert.Equal(approvalJson, retained.ApprovalJson); Assert.Equal(reviewJson, retained.ApprovalRequestJson);
        AgentToolRecord tool = (await fixture.Store.GetToolCallAsync(source.Address, intent.Id, token))!;
        Assert.Equal("旧参数", tool.Prepared!.Arguments!.Value.GetProperty("value").GetString());
        Assert.Equal("task_initiator", Assert.Single(tool.ApprovalRequest!.Evidence).Kind);
        OutgoingBatchSnapshot outgoing = (await fixture.Store.GetBatchAsync(source.Address, batch.Id, token))!;
        Assert.True(outgoing.Result.Confirmed); Assert.Equal(sent.Id, Assert.Single(outgoing.Result.Messages).MessageId);
        Assert.Equal("旧批次正文", Assert.Single(outgoing.FinalMessages).Segments[0].Text);
        Assert.Equal("历史审批原结论", outgoing.Approval!.Reason); Assert.Equal(intent.Id, outgoing.Original.Origin.ToolInvocationId);
        Assert.Single((await fixture.Store.SearchAsync(new HistorySearchQuery { Conversation = source.Address, Text = "旧输入", Kinds = [HistoryRecordKind.AgentInput] }, token)).Hits);
        Assert.False(database.Database.HasPendingModelChanges());
    }

    /// <summary>构造实际共享配置，存储测试不发起模型或平台网络请求。</summary>
    private static ConversationProfileSnapshot Profile(AgentBudget? budget = null)
    {
        ConversationConfiguration configuration = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1"));
        AgentProfile agent = new()
        {
            Enabled = true,
            Models = ["A"],
            Tools = [AgentToolIds.Spawn, AgentToolIds.FinishTurn],
            DefaultTemplate = "worker",
            Budget = budget ?? new AgentBudget(),
            Templates = [new SubagentTemplate { Id = "worker", Description = "合成任务执行者", Instructions = "仅处理合成输入。", Models = ["A"] }]
        };
        return new ConversationProfileSnapshot
        {
            Revision = 1,
            Profile = configuration.Profiles[0] with { Agent = agent },
            Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableDictionary(model => model.Id),
            Providers = configuration.Providers.ToImmutableDictionary(provider => provider.Id)
        };
    }

    /// <summary>真实身份由存储从消息记录取得，调用参数不包含可伪造的发言者或地址。</summary>
    private static AgentWakeAdmission Request(TestStore fixture, MessageSnapshot message, ConversationProfileSnapshot profile) => new()
    {
        WakeRequest = new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments },
        Profile = profile,
        RunId = fixture.RunId,
        NewTaskId = Guid.NewGuid(),
        NewRootAgentId = Guid.NewGuid(),
        Now = TestStore.Now
    };
}
