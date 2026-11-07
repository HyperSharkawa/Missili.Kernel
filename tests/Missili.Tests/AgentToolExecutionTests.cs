using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;
using Missili.Agents;
using Missili.Persistence;
using Missili.Nexus;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>工具执行通过真实 SDK、实际插件 DLL 与 SQLite，验证执行边界而不是模拟返回成功。</summary>
public sealed class AgentToolExecutionTests
{
    /// <summary>三种协议真实执行工具后，以完整原生状态和实际结果进行下一轮模型调用。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions, false)]
    [InlineData(ModelProtocol.ChatCompletions, true)]
    [InlineData(ModelProtocol.Responses, false)]
    [InlineData(ModelProtocol.Responses, true)]
    [InlineData(ModelProtocol.Anthropic, false)]
    [InlineData(ModelProtocol.Anthropic, true)]
    public async Task RealToolExecutionContinuesAllProtocols(ModelProtocol protocol, bool stream)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument native = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(protocol).Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal));
        ImmutableArray<string> events = ModelProtocolRoundTripTests.Events(protocol, native.RootElement).Select(value => value.Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal)).ToImmutableArray();
        await using ToolFixture fixture = await ToolFixture.CreateAsync(protocol,
            [new ModelMockStep { Response = native.RootElement.Clone(), Events = events }, new ModelMockStep { Text = "实际工具已完成" }], "tool", false, token);
        ModelResult model = await fixture.GenerateAsync(stream, token);
        AgentToolGroupResult tools = await fixture.ExecuteAsync(model, token);
        Assert.True(tools.CanContinue, tools.Error);
        AgentToolResult result = Assert.Single(tools.Results);
        Assert.Equal(ToolCallState.Succeeded, result.State);
        Assert.Equal(42, result.Execution!.Data!.Value.GetProperty("value").GetInt32());
        Assert.True(await fixture.Agents.ReserveCallsAsync(fixture.AgentId, 1, 0, true, false, token));
        ModelResult final = await fixture.Runtime.Models.GenerateAsync(fixture.Request with
        {
            Id = Guid.NewGuid(),
            Stream = stream,
            Input = fixture.Request.Input with
            {
                Items = fixture.Request.Input.Items.AddRange(new ModelInputItem[]
            { new ModelTurnInput { Message = model.ToAssistantMessage() }, new ModelTurnInput { Message = tools.Message! } })
            }
        }, cancellationToken: token);
        Assert.Equal("实际工具已完成", final.Text);
        Assert.Equal(ModelCallState.Succeeded, final.State);
        AgentToolRecord record = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
        Assert.NotNull(record.StartedAt); Assert.NotNull(record.FinishedAt);
        Assert.Equal(fixture.AgentId, record.Origin.AgentId);
        Assert.Equal("call-one", record.Intent.ToolCallId);
        Assert.Equal(ToolApprovalState.NotRequired, record.Approval!.State);
        Assert.Empty(fixture.RuntimeFixture.Transport.Requests);
    }

    /// <summary>两个工具实际进入网络等待区间；乱序完成不改变协议结果顺序，重入不再执行。</summary>
    [Fact]
    public async Task ParallelToolsOverlapAndReentryDoesNotRepeatWork()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(TwoTools);
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { Response = response.RootElement.Clone() }, new ModelMockStep { WaitForSignal = "first" }, new ModelMockStep { WaitForSignal = "second" }], "tool-wait", false, token);
        ModelResult model = await fixture.GenerateAsync(false, token);
        await fixture.Mock.NextRequestAsync(token);
        Task<AgentToolGroupResult> work = fixture.ExecuteAsync(model, token);
        await fixture.Mock.NextRequestAsync(token);
        await fixture.Mock.NextRequestAsync(token);
        try
        {
            Assert.False(work.IsCompleted);
            fixture.Mock.Release("second");
            AgentToolGroupResult duplicate = await fixture.ExecuteAsync(model, token);
            Assert.False(duplicate.CanContinue);
            Assert.Equal(2, duplicate.Results.Length);
            Assert.Equal(3, fixture.Mock.TotalRequests);
        }
        finally { fixture.Mock.Release("first"); fixture.Mock.Release("second"); }
        AgentToolGroupResult completed = await work.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(completed.CanContinue, completed.Error);
        Assert.Equal(new[] { 42, 43 }, completed.Results.Select(item => item.Execution!.Data!.Value.GetProperty("value").GetInt32()));
        Assert.Equal(new[] { "one", "two" }, completed.Message!.Content.Select(item => item.ToolCallId));
        AgentTaskSnapshot task = (await fixture.Agents.GetTaskAsync(TestStore.Address(), fixture.TaskId, token))!;
        Assert.Equal(3, task.Usage.ModelCalls); Assert.Equal(2, task.Usage.ToolCalls);
        Assert.False((await fixture.ExecuteAsync(model, token)).CanContinue);
        Assert.Equal(3, fixture.Mock.TotalRequests);
    }

    /// <summary>只有合法放行开始执行；拒绝或无效审批结果保留原因且不访问备用审批模型。</summary>
    [Theory]
    [InlineData("allow", ToolCallState.Succeeded)]
    [InlineData("deny", ToolCallState.ApprovalDenied)]
    [InlineData("ask", ToolCallState.ApprovalFailed)]
    public async Task ApprovalControlsActualExecution(string decision, ToolCallState state)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [SingleTool(), new ModelMockStep { Text = JsonSerializer.Serialize(new Dictionary<string, string> { ["decision"] = decision, ["reason"] = "合成审查意见" }) }], "tool", true, token);
        AgentToolGroupResult group = await fixture.ExecuteAsync(await fixture.GenerateAsync(false, token), token);
        Assert.True(group.CanContinue, group.Error);
        AgentToolResult result = Assert.Single(group.Results);
        Assert.Equal(state, result.State);
        AgentToolRecord call = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
        Assert.Equal(decision == "allow", call.StartedAt.HasValue);
        Assert.Equal(2, fixture.Mock.TotalRequests);
        Assert.Equal(call.Prepared!.Arguments!.Value.GetRawText(), call.ApprovalRequest!.Prepared.Arguments!.Value.GetRawText());
        Assert.Contains(call.ApprovalRequest.Evidence, item => item.Kind == "user_message" && item.SenderId == "synthetic-user");
    }

    /// <summary>意图和审批记录失败时没有执行，结果记录失败时保留已取得内容且不继续模型。</summary>
    [Theory]
    [InlineData("intent")]
    [InlineData("approval")]
    [InlineData("result")]
    public async Task StorageFailuresKeepExactExecutionBoundary(string stage)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions, [SingleTool()], "tool", false, token);
        ModelResult model = await fixture.GenerateAsync(false, token);
        await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(fixture.RuntimeFixture.Root, "history.db"), ForeignKeys = true, Pooling = false }.ToString()).Options);
        string trigger = stage switch
        {
            "intent" => "CREATE TRIGGER RejectTool BEFORE INSERT ON AgentToolCalls BEGIN SELECT RAISE(ABORT, 'synthetic'); END;",
            "approval" => "CREATE TRIGGER RejectTool BEFORE UPDATE OF ApprovalJson ON AgentToolCalls WHEN NEW.ApprovalJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'synthetic'); END;",
            _ => "CREATE TRIGGER RejectTool BEFORE UPDATE OF ExecutionJson ON AgentToolCalls WHEN NEW.ExecutionJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'synthetic'); END;"
        };
        await database.Database.ExecuteSqlRawAsync(trigger, token);
        AgentToolGroupResult group = await fixture.ExecuteAsync(model, token);
        Assert.False(group.CanContinue);
        if (stage == "intent") Assert.Empty(group.Results);
        else
        {
            AgentToolResult result = Assert.Single(group.Results);
            Assert.False(result.Recorded);
            Assert.Equal(stage == "result", result.Execution is not null);
            AgentToolRecord call = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
            Assert.Equal(stage == "result", call.StartedAt.HasValue);
            Assert.Equal(ToolCallState.RecordingFailed, call.State);
        }
        Assert.Equal(1, fixture.Mock.TotalRequests);
    }

    /// <summary>审批期间重载工具使旧批准失效，旧请求不进入新代次执行。</summary>
    [Fact]
    public async Task ReloadDuringApprovalDoesNotRunNewGeneration()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [SingleTool(), new ModelMockStep { WaitForSignal = "approval", Text = "{\"decision\":\"allow\",\"reason\":\"合成放行\"}" }], "tool", true, token);
        ModelResult model = await fixture.GenerateAsync(false, token);
        await fixture.Mock.NextRequestAsync(token);
        Task<AgentToolGroupResult> work = fixture.ExecuteAsync(model, token);
        await fixture.Mock.NextRequestAsync(token);
        string updated = await fixture.RuntimeFixture.PackageAsync("tool", "v2", token);
        await fixture.Runtime.Plugins.ReloadAsync("tool", updated, RuntimeFixture.Configuration("tool"), token);
        fixture.Mock.Release("approval");
        AgentToolResult result = Assert.Single((await work.WaitAsync(TimeSpan.FromSeconds(10), token)).Results);
        Assert.Equal(ToolCallState.Unavailable, result.State);
        Assert.Null(result.Execution);
        Assert.Null((await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!.StartedAt);
    }

    /// <summary>批准只绑定相关权限与政策，无关配置更新不使已有具体动作重复审批。</summary>
    [Theory]
    [InlineData("unrelated", ToolCallState.Succeeded)]
    [InlineData("permission", ToolCallState.Rejected)]
    [InlineData("policy", ToolCallState.Rejected)]
    public async Task RelevantConfigurationChangesInvalidateApproval(string change, ToolCallState expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [SingleTool(), new ModelMockStep { WaitForSignal = "approval", Text = "{\"decision\":\"allow\",\"reason\":\"合成具体动作获准\"}" }], "tool", true, token);
        ModelResult model = await fixture.GenerateAsync(false, token);
        await fixture.Mock.NextRequestAsync(token);
        Task<AgentToolGroupResult> work = fixture.ExecuteAsync(model, token);
        await fixture.Mock.NextRequestAsync(token);
        ConversationProfileSnapshot captured = fixture.Context.Profile;
        ConversationProfile profile = change switch
        {
            "permission" => captured.Profile with { Agent = captured.Profile.Agent with { Tools = [] } },
            "policy" => captured.Profile with { Approvals = captured.Profile.Approvals with { Policy = new ToolApprovalPolicy { CustomText = "更新后的独立政策" } } },
            _ => captured.Profile with { MaximumMessages = captured.Profile.MaximumMessages + 10 }
        };
        await fixture.RuntimeFixture.WriteModelsAsync(new ConversationConfiguration
        {
            DefaultProfileId = profile.Id,
            Providers = captured.Providers.Values.ToImmutableArray(),
            Models = captured.Models.Values.ToImmutableArray(),
            Profiles = [profile]
        }, token);
        await fixture.Runtime.Profiles.ReloadAsync(token);
        fixture.Mock.Release("approval");
        AgentToolResult result = Assert.Single((await work.WaitAsync(TimeSpan.FromSeconds(15), token)).Results);
        Assert.Equal(expected, result.State);
        AgentToolRecord call = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
        Assert.Equal(expected == ToolCallState.Succeeded, call.StartedAt.HasValue);
        Assert.Equal(2, fixture.Mock.TotalRequests);
    }

    /// <summary>传输诊断超限时模型得到读取引用，完整回执保留且平台动作不重复。</summary>
    [Fact]
    public async Task FinalReceiptCannotOverflowModelResultBudget()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(ReplyTool);
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { Response = response.RootElement.Clone() }, new ModelMockStep { Model = "B", Text = "完整生成正文" },
                new ModelMockStep { Text = "{\"decision\":\"allow\",\"reason\":\"允许本次发送\"}" }], "reply", false, token);
        string diagnostic = new('x', 70000);
        fixture.RuntimeFixture.Transport.OnSend = (_, _, _) => Task.FromResult(new TransportResult { State = DeliveryState.Unknown, Error = diagnostic });
        AgentToolGroupResult group = await fixture.ExecuteAsync(await fixture.GenerateAsync(false, token), token);
        AgentToolResult result = Assert.Single(group.Results);
        Assert.True(group.CanContinue, group.Error);
        Assert.Equal("OutputLimit", result.ErrorKind);
        Assert.Equal(BatchSendState.Unconfirmed, result.Sending!.State);
        Assert.Equal(diagnostic, result.Sending.Error);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(group.Message!.Content[0].Data!.Value.GetRawText()) <= 65536);
        AgentToolRecord saved = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
        Assert.Equal(diagnostic, saved.Result!.Sending!.Error);
        Assert.Single(fixture.RuntimeFixture.Transport.Requests);
    }

    /// <summary>生成和后处理来自不同 DLL；生成资格释放后重载不撤销宿主已接管的草稿。</summary>
    [Fact]
    public async Task ReplyDraftSurvivesGeneratorReloadDuringFinalApproval()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(ReplyTool);
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { Response = response.RootElement.Clone() }, new ModelMockStep { Model = "B", Text = "原词第一句。第二句。" },
                new ModelMockStep { WaitForSignal = "approval", Text = "{\"decision\":\"allow\",\"reason\":\"最终批次符合要求\"}" }], "reply", false, token);
        ModelResult model = await fixture.GenerateAsync(false, token);
        await fixture.Mock.NextRequestAsync(token);
        Task<AgentToolGroupResult> work = fixture.ExecuteAsync(model, token);
        ModelMockRequest generation = await fixture.Mock.NextRequestAsync(token);
        ModelMockRequest approval = await fixture.Mock.NextRequestAsync(token);
        Assert.Equal("B", generation.Model);
        Assert.Contains("专用回复提示词", generation.Body!.Value.ToString());
        Assert.False(generation.Body.Value.TryGetProperty("tools", out JsonElement unused));
        Assert.Empty(fixture.RuntimeFixture.Transport.Requests);
        string package = OfficialPluginPackages.GetPath("reply");
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("missili.tools.reply", package, RuntimeFixture.Configuration(), token)).State);
        WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("missili.tools.reply")!;
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
        fixture.Mock.Release("approval");
        AgentToolGroupResult group = await work.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(group.CanContinue, group.Error);
        AgentToolResult result = Assert.Single(group.Results);
        Assert.Equal(ToolCallState.Succeeded, result.State);
        Assert.True(result.Sending!.Confirmed, result.Error);
        Assert.Equal(new[] { "新词第一句", "。第二句。" }, fixture.RuntimeFixture.Transport.Requests.Select(message => message.Segments[0].Text));
        Assert.Equal(2, result.Sending.Messages.Length);
        Assert.Equal(3, (await fixture.Agents.GetTaskAsync(TestStore.Address(), fixture.TaskId, token))!.Usage.ModelCalls);
        AgentToolRecord record = (await fixture.Store.GetToolCallAsync(TestStore.Address(), result.InvocationId, token))!;
        Assert.Equal("原词第一句。第二句。", record.Execution!.Drafts.Single().Segments[0].Text);
        OutgoingBatchSnapshot batch = (await ((IOutgoingStore)fixture.Runtime.History).GetBatchAsync(TestStore.Address(), result.Sending.BatchId, token))!;
        Assert.Equal(ToolApprovalState.Allowed, batch.Approval!.State);
        Assert.Equal("missili.plugins.reply-processing", Assert.Single(batch.Processors).Key.PluginId);
    }

    /// <summary>回复模型的截断或意外工具请求不能生成草稿，也不进入发送审批。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteOrToolReplyIsNeverSent(bool truncated)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse(ReplyTool);
        ModelMockStep second = truncated ? new ModelMockStep { Truncated = true, Text = "只取得部分回复" } : SingleTool();
        await using ToolFixture fixture = await ToolFixture.CreateAsync(ModelProtocol.ChatCompletions,
            [new ModelMockStep { Response = response.RootElement.Clone() }, second], "reply", false, token);
        AgentToolGroupResult group = await fixture.ExecuteAsync(await fixture.GenerateAsync(false, token), token);
        Assert.True(group.CanContinue, group.Error);
        AgentToolResult result = Assert.Single(group.Results);
        Assert.Equal(ToolCallState.Failed, result.State);
        Assert.Empty(result.Execution!.Drafts);
        Assert.Empty(fixture.RuntimeFixture.Transport.Requests);
        Assert.Equal(2, fixture.Mock.TotalRequests);
    }

    /// <summary>协议响应直接按公开协议构造，不复用生产编码器。</summary>
    private static ModelMockStep SingleTool()
    {
        using JsonDocument document = JsonDocument.Parse(ModelProtocolRoundTripTests.NativeResponse(ModelProtocol.ChatCompletions).Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal));
        return new ModelMockStep
        {
            Response = document.RootElement.Clone(),
            Events = ModelProtocolRoundTripTests.Events(ModelProtocol.ChatCompletions, document.RootElement)
            .Select(value => value.Replace("synthetic_tool", "fixture_tool", StringComparison.Ordinal)).ToImmutableArray()
        };
    }

    private const string TwoTools = """{"id":"chat-tools","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"one","type":"function","function":{"name":"fixture_tool","arguments":"{\"value\":42}"}},{"id":"two","type":"function","function":{"name":"fixture_tool","arguments":"{\"value\":43}"}}]},"finish_reason":"tool_calls"}]}""";
    private const string ReplyTool = """{"id":"chat-reply","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"reply-one","type":"function","function":{"name":"reply","arguments":"{\"instructions\":\"完整表达成果\",\"facts\":[\"合成事实\"]}"}}]},"finish_reason":"tool_calls"}]}""";

    /// <summary>实际接单和模型记录构成执行前提；仅外部业务对端使用协议 Mock。</summary>
    private sealed class ToolFixture : IAsyncDisposable
    {
        private readonly string _variable;
        public RuntimeFixture RuntimeFixture { get; }
        public Missili.Kernel.InfrastructureRuntime Runtime => RuntimeFixture.Runtime;
        public ModelMockServer Mock { get; }
        public IAgentStore Agents => (IAgentStore)Runtime.History;
        public IAgentToolStore Store => (IAgentToolStore)Runtime.History;
        public Guid AgentId { get; }
        public Guid TaskId { get; }
        public Guid TurnId { get; } = Guid.NewGuid();
        private Guid _modelEntryId;
        private readonly AgentWorkLimiter _working = new(1);
        public ModelRequest Request { get; private set; } = null!;
        public AgentToolContext Context { get; private set; } = null!;

        /// <summary>已完成初始化的测试资源由一个所有者释放。</summary>
        private ToolFixture(RuntimeFixture fixture, ModelMockServer mock, string variable, Guid taskId, Guid agentId)
        { RuntimeFixture = fixture; Mock = mock; _variable = variable; TaskId = taskId; AgentId = agentId; }

        /// <summary>创建真实任务、插件和可核验的模型归属。</summary>
        public static async Task<ToolFixture> CreateAsync(ModelProtocol protocol, ImmutableArray<ModelMockStep> steps, string mode, bool approval, CancellationToken token)
        {
            ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = steps }, "http://127.0.0.1:0", token);
            string variable = "MISSILI_TOOL_EXECUTION_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, "synthetic");
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol);
            configuration = configuration with
            {
                Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile { Enabled = true, Models = ["A"], Tools = ["fixture.tool"] },
                    Approvals = new ToolApprovalOptions { Enabled = approval, Models = ["A", "B"] } }]
            };
            if (mode == "reply")
            {
                using JsonDocument reply = JsonDocument.Parse("""{"models":["B"],"systemPrompt":"专用回复提示词","stream":true}""");
                using JsonDocument processing = JsonDocument.Parse("""{"steps":[{"type":"replace","pattern":"原词","replacement":"新词"},{"type":"split","maximumTextElements":5}]}""");
                configuration = configuration with
                {
                    Profiles = [configuration.Profiles[0] with
                {
                    Agent = configuration.Profiles[0].Agent with { Tools = ["reply.generate"] },
                    Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add("missili.tools.reply", reply.RootElement.Clone()).Add("missili.plugins.reply-processing", processing.RootElement.Clone()),
                    Outgoing = new OutgoingOptions { Processors = [new HandlerKey { PluginId = "missili.plugins.reply-processing", HandlerId = "process" }] },
                    Approvals = new ToolApprovalOptions { Models = ["A"], Tools = ImmutableDictionary<string, ToolApprovalOverride>.Empty.Add(ApprovalActionIds.MessageSend, new ToolApprovalOverride { Enabled = true }) }
                }]
                };
            }
            RuntimeFixture runtime = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(runtime, protocol, token);
            string package = mode == "reply" ? OfficialPluginPackages.GetPath("reply") : await runtime.PackageAsync("tool", "v1", token, concurrency: 2);
            Assert.Equal(PluginState.Active, (await runtime.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(mode), token)).State);
            if (mode == "reply") Assert.Equal(PluginState.Active, (await runtime.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("reply-processing"), RuntimeFixture.Configuration(), token)).State);
            MessageSnapshot message = (await runtime.Runtime.ReceiveAsync(TestStore.Message("请执行合成工具") with { SenderId = "synthetic-user" }, token)).Message!;
            ConversationProfileSnapshot profile = runtime.Runtime.Profiles.Resolve(message.Address);
            AgentWakeResult admitted = await ((IAgentStore)runtime.Runtime.History).RegisterWakeAsync(new AgentWakeAdmission
            {
                WakeRequest = new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = message.Segments },
                Profile = profile,
                RunId = Guid.NewGuid(),
                NewTaskId = Guid.NewGuid(),
                NewRootAgentId = Guid.NewGuid(),
                Now = TestStore.Now
            }, token);
            ToolFixture fixture = new(runtime, mock, variable, admitted.TaskId!.Value, admitted.AgentId!.Value);
            OperationOrigin origin = new() { TaskId = fixture.TaskId, AgentId = fixture.AgentId, TurnId = fixture.TurnId };
            fixture.Context = new AgentToolContext
            {
                Origin = origin,
                Conversation = message.Address,
                Profile = profile,
                Models = runtime.Runtime.Models,
                History = runtime.Runtime.History,
                AgentHistory = fixture.Agents,
                Contents = runtime.Runtime.Contents,
                Media = runtime.Runtime.Media,
                Transcriptions = runtime.Runtime.Models.Transcriptions,
                Clock = TimeProvider.System,
                ToolHistory = fixture.Store,
                Search = (IHistorySearch)runtime.Runtime.History,
                Assets = (IConversationAssetAccess)runtime.Runtime.History
            };
            fixture.Request = new ModelRequest
            {
                Purpose = "agent",
                Profile = profile,
                Candidates = ["A"],
                Conversation = message.Address,
                Origin = origin,
                Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("请执行合成工具")] }]),
                Tools = runtime.Runtime.Tools.CaptureTools().Select(tool => new ModelTool { Name = tool.Definition.Name, Description = tool.Definition.Description, Parameters = tool.Definition.Parameters }).ToImmutableArray()
            };
            return fixture;
        }

        /// <summary>每次决策真实消耗预算，输出通过模型调用记录持久化。</summary>
        public async Task<ModelResult> GenerateAsync(bool stream, CancellationToken token)
        {
            Assert.True(await Agents.ReserveCallsAsync(AgentId, 1, 0, true, false, token));
            ModelResult result = await Runtime.Models.GenerateAsync(Request with { Stream = stream }, cancellationToken: token);
            Assert.Equal(ModelCallState.Succeeded, result.State);
            _modelEntryId = (await Agents.AppendEntryAsync(AgentId, AgentEntryKind.ModelOutput, JsonSerializer.SerializeToElement(result), TestStore.Now, token)).Id;
            return result;
        }

        /// <summary>调用生产执行器，真实工具调度、审批及记录不能被夹具绕开。</summary>
        public Task<AgentToolGroupResult> ExecuteAsync(ModelResult model, CancellationToken token)
            => Runtime.ToolExecutor.ExecuteAsync(Context, TurnId, _modelEntryId, model, Runtime.Tools.CaptureTools(), _working, token, token);

        /// <summary>释放所有测试控制信号后等待实际退出，清理独占的合成凭据变量。</summary>
        public async ValueTask DisposeAsync()
        {
            Mock.Release("first"); Mock.Release("second"); Mock.Release("approval");
            try { await RuntimeFixture.DisposeAsync(); await Mock.DisposeAsync(); }
            finally { _working.Dispose(); Environment.SetEnvironmentVariable(_variable, null); }
        }
    }
}
