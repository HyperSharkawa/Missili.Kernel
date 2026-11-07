using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Nexus;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 SQLite、插件 DLL 和发送服务共同验证批次处理与回执边界。</summary>
public sealed class OutgoingPipelineTests
{
    /// <summary>拆分结果只向后传播，各消息独立入库；旧处理器实际卸载后内容仍可读取。</summary>
    [Fact]
    public async Task SplitFlowsForwardOnceAndGenerationIsCollected()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration());
        await LoadAsync(fixture, "outgoing-split", token);
        OutgoingBatch batch = Text("甲|乙|丙");
        BatchSendResult result = await fixture.Runtime.Sender.SendAsync(batch, token);
        Assert.True(result.Confirmed, result.Error);
        Assert.Equal(3, result.TotalMessages);
        Assert.Equal(new[] { "甲:v1", "乙:v1", "丙:v1" }, fixture.Transport.Requests.Select(message => message.Segments[0].Text));
        OutgoingBatchSnapshot saved = (await Store(fixture).GetBatchAsync(batch.Address, batch.Id, token))!;
        Assert.Equal("甲|乙|丙", saved.Original.Messages[0].Segments[0].Text);
        Assert.Equal(2, saved.Processors.Length);
        Assert.Equal(3, saved.FinalMessages.Length);
        Assert.Equal(3, (await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages.Length);
        string second = await fixture.PackageAsync("processor", "v2", token, concurrency: 1);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("processor", second, RuntimeFixture.Configuration("outgoing-split"), token)).State);
        Assert.Equal("丁:v2", (await fixture.Runtime.Sender.SendAsync(Text("丁"), token)).Confirmed ? fixture.Transport.Requests.Last().Segments[0].Text : "发送失败");
        WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("processor")!;
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
    }

    /// <summary>失败、缺失或重入不会绕过处理链发送原文；过滤也保留处理来源。</summary>
    [Theory]
    [InlineData("outgoing-fail", BatchSendState.Failed)]
    [InlineData("outgoing-reenter", BatchSendState.Failed)]
    [InlineData("outgoing-filter", BatchSendState.Filtered)]
    [InlineData("missing", BatchSendState.Failed)]
    [InlineData("outgoing-init-fail", BatchSendState.Failed)]
    public async Task ProcessingCannotFallBackToRawContent(string mode, BatchSendState state)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration());
        if (mode != "missing") await LoadAsync(fixture, mode, token);
        OutgoingBatch batch = Text("不应发送的原文");
        BatchSendResult result = await fixture.Runtime.Sender.SendAsync(batch, token);
        Assert.Equal(state, result.State);
        Assert.Empty(fixture.Transport.Requests);
        OutgoingBatchSnapshot saved = (await Store(fixture).GetBatchAsync(batch.Address, batch.Id, token))!;
        Assert.Equal(state, saved.Result.State);
        Assert.NotNull(saved.Processing);
        Assert.Equal(2, saved.Processing.Options.Processors.Length);
        Assert.True(saved.Processing.ConfigurationRevision > 0);
        Assert.True(saved.Processing.PluginConfigurations.ContainsKey("processor"));
        if (state == BatchSendState.Filtered)
        { Assert.Single(saved.Processors); Assert.Empty(saved.FinalMessages); }
    }

    /// <summary>第二条未知或异步受理时停止第三条，迟到确认只改读取结果，不补发。</summary>
    [Theory]
    [InlineData(DeliveryState.Unknown)]
    [InlineData(DeliveryState.Accepted)]
    public async Task UnconfirmedSecondMessageStopsBatchAndLateReceiptDoesNotResume(DeliveryState state)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration());
        await LoadAsync(fixture, "outgoing-split", token);
        fixture.Transport.OnSend = (id, message, _) => Task.FromResult(new TransportResult
        { State = message.BatchPosition == 1 ? state : DeliveryState.Confirmed, PlatformMessageId = message.BatchPosition == 0 ? "sent-" + id : null });
        OutgoingBatch batch = Text("甲|乙|丙");
        BatchSendResult result = await fixture.Runtime.Sender.SendAsync(batch, token);
        Assert.Equal(BatchSendState.Unconfirmed, result.State);
        Assert.Equal(2, result.UnattemptedFrom);
        Assert.Equal(2, fixture.Transport.Requests.Count);
        await ((ISendReceiptSink)fixture.Runtime.Sender).RecordReceiptAsync(result.Messages[1].MessageId!.Value,
            new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "late" }, token);
        OutgoingBatchSnapshot saved = (await Store(fixture).GetBatchAsync(batch.Address, batch.Id, token))!;
        Assert.Equal(BatchSendState.PartiallySent, saved.Result.State);
        Assert.All(saved.Result.Messages, message => Assert.Equal(DeliveryState.Confirmed, message.State));
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.Null(await Store(fixture).GetBatchAsync(TestStore.Address("other"), batch.Id, token));
        BatchSendResult duplicate = await fixture.Runtime.Sender.SendAsync(batch, token);
        Assert.False(duplicate.Confirmed);
        Assert.Equal(2, fixture.Transport.Requests.Count);
    }

    /// <summary>发送审批检查实际变换后的整批内容，拒绝不会发出任意分片。</summary>
    [Theory]
    [InlineData("allow", BatchSendState.Completed, 2)]
    [InlineData("deny", BatchSendState.Rejected, 0)]
    [InlineData("ask", BatchSendState.Failed, 0)]
    public async Task ApprovalReviewsFinalBatch(string decision, BatchSendState expected, int sends)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { StrictSteps = true, Steps = [new ModelMockStep { Text = JsonSerializer.Serialize(new Dictionary<string, string> { ["decision"] = decision, ["reason"] = "合成审批意见" }) }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_OUTGOING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(mock.BaseAddress, variable);
            configuration = configuration with
            {
                Profiles = [configuration.Profiles[0] with
            { Approvals = new ToolApprovalOptions { Models = ["A", "B"], Tools = ImmutableDictionary<string, ToolApprovalOverride>.Empty.Add(ApprovalActionIds.MessageSend, new ToolApprovalOverride { Enabled = true }) } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await LoadAsync(fixture, "outgoing-split", token);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            OutgoingBatch batch = Text("第一段|第二段");
            BatchSendResult result = await fixture.Runtime.Sender.SendAsync(batch, token);
            Assert.True(expected == result.State, JsonSerializer.Serialize(result));
            Assert.Equal(sends, fixture.Transport.Requests.Count);
            JsonElement user = mock.Requests.Single().Body!.Value.GetProperty("messages").EnumerateArray().Single(message => message.GetProperty("role").GetString() == "user");
            JsonElement content = user.GetProperty("content");
            string evidence = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            using JsonDocument action = JsonDocument.Parse(evidence);
            JsonElement messages = action.RootElement.GetProperty("Arguments").GetProperty("messages");
            Assert.Equal(new[] { "第一段:v1", "第二段:v1" }, messages.EnumerateArray().Select(message => message.GetProperty("Segments")[0].GetProperty("Text").GetString()));
            OutgoingBatchSnapshot saved = (await Store(fixture).GetBatchAsync(batch.Address, batch.Id, token))!;
            Assert.NotNull(saved.Approval);
            Assert.Equal(ApprovalActionIds.MessageSend, saved.ApprovalRequest!.ToolId);
            Assert.Equal(2, saved.ApprovalRequest.Prepared.Arguments!.Value.GetProperty("messages").GetArrayLength());
            Assert.Equal(2, saved.FinalMessages.Length);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>已停止任务的完整正文也不能成为新的发送动作。</summary>
    [Fact]
    public async Task StoppingAgentCannotStartSending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration configuration = Configuration() with
        {
            Profiles = [new ConversationProfile
        { Id = "default", Agent = new AgentProfile { Enabled = true, Models = ["A"] } }]
        };
        configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray() };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
        MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message("合成任务"), token)).Message!;
        IAgentStore agents = (IAgentStore)fixture.Runtime.History;
        Guid run = Guid.NewGuid();
        AgentWakeResult admission = await agents.RegisterWakeAsync(new AgentWakeAdmission
        {
            WakeRequest = new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments },
            Profile = fixture.Runtime.Profiles.Resolve(message.Address),
            RunId = run,
            NewTaskId = Guid.NewGuid(),
            NewRootAgentId = Guid.NewGuid(),
            Now = TestStore.Now
        }, token);
        Assert.True(admission.Registered, admission.Error);
        await agents.RequestStopAsync(admission.TaskId!.Value, admission.AgentId!.Value, [admission.AgentId.Value], TestStore.Now, TestStore.Now.AddSeconds(180), token);
        BatchSendResult result = await fixture.Runtime.Sender.SendAsync(Text("已经停止") with { Origin = new OperationOrigin { TaskId = admission.TaskId, AgentId = admission.AgentId } }, token);
        Assert.Equal(BatchSendState.Cancelled, result.State);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>使用独立目录中的配置，只有明确的两个处理器参与批次。</summary>
    private static ConversationConfiguration Configuration(Uri? address = null, string variable = "MISSILI_SYNTHETIC_UNUSED")
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address ?? new Uri("http://127.0.0.1:1/v1"), variable, ModelProtocol.ChatCompletions);
        return configuration with
        {
            Profiles = [configuration.Profiles[0] with { Outgoing = new OutgoingOptions
        { Processors = [new HandlerKey { PluginId = "processor", HandlerId = "transform" }, new HandlerKey { PluginId = "processor", HandlerId = "suffix" }] } }]
        };
    }

    /// <summary>加载真实单并发插件，检查处理器注册没有引入重入额度死锁。</summary>
    private static async Task<string> LoadAsync(RuntimeFixture fixture, string mode, CancellationToken token)
    {
        string package = await fixture.PackageAsync("processor", "v1", token, concurrency: 1);
        PluginStatus status = await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(mode), token);
        Assert.Equal(mode == "outgoing-init-fail" ? PluginState.Faulted : PluginState.Active, status.State);
        return package;
    }

    /// <summary>测试通过实际存储契约查询审计，不直接读取实体。</summary>
    private static IOutgoingStore Store(RuntimeFixture fixture) => (IOutgoingStore)fixture.Runtime.History;

    /// <summary>提供标准文本批次。</summary>
    private static OutgoingBatch Text(string text) => OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text(text)]);
}
