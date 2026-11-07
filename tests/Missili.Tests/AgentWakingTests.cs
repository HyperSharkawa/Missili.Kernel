using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Kernel;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实入口 DLL、模型协议与 SQLite 验证四种唤醒和独立命令。</summary>
public sealed class AgentWakingTests
{
    private const string PluginId = "missili.plugins.agent-chat";

    /// <summary>规则按前缀、提及、关键词、概率短路，原消息和冻结输入分别保留。</summary>
    [Theory]
    [InlineData("{}", "/agent你好", AgentWakeKind.Prefix, "你好")]
    [InlineData("{}", "/agent", AgentWakeKind.Prefix, "")]
    [InlineData("{}", "/agent new 普通文字", AgentWakeKind.Prefix, " new 普通文字")]
    [InlineData("{}", "/agent stopx", AgentWakeKind.Prefix, " stopx")]
    [InlineData("""{"wakePrefixes":["/","/agent"]}""", "/agent你好", AgentWakeKind.Prefix, "你好")]
    [InlineData("""{"wakePrefixes":["/"]}""", "/你好", AgentWakeKind.Prefix, "你好")]
    [InlineData("""{"wakePrefixes":[],"keywords":["米粒"]}""", "今天想和米粒聊天", AgentWakeKind.Keyword, "今天想和米粒聊天")]
    [InlineData("""{"wakePrefixes":[],"keywords":["Missili"]}""", "MISSILI hello", AgentWakeKind.Keyword, "MISSILI hello")]
    [InlineData("""{"keywords":["a.b"]}""", "a.b", AgentWakeKind.Keyword, "a.b")]
    [InlineData("""{"keywords":["你好"],"probability":{"enabled":true,"value":1}}""", "/agent你好", AgentWakeKind.Prefix, "你好")]
    [InlineData("""{"wakePrefixes":[],"probability":{"enabled":true,"value":1}}""", "普通闲聊", AgentWakeKind.Probability, "普通闲聊")]
    public async Task WakeRecordsReasonAndNeverPromisesTask(string settings, string text, AgentWakeKind kind, string projected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(settings, token);
        IncomingMessage input = TestStore.Message(text, "same-message");
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(input, token)).Message!;
        ReceiveReceipt repeated = await session.Fixture.Runtime.ReceiveAsync(input, token);
        Assert.True(repeated.Duplicate);
        AgentWakeResult wake = await session.FinishedAsync(source, token);
        Assert.Equal(1, session.Model.TotalRequests);
        Assert.Single(session.Fixture.Transport.Requests);
        AgentEntryPage page = await ((IAgentHistory)session.Fixture.Runtime.History).ReadEntriesAsync(source.Address, wake.TaskId!.Value, wake.AgentId!.Value, 0, 100, token);
        AgentWakeInput stored = Assert.Single(page.Entries, entry => entry.Kind == AgentEntryKind.WakeInput).Data.Deserialize<AgentWakeInput>()!;
        Assert.Equal(kind, stored.Cause.Kind);
        Assert.Equal(projected, string.Concat(stored.Content.Select(segment => segment.Text)));
        Assert.Equal(text, (await session.Fixture.Runtime.History.GetAsync(source.Id, token))!.Segments[0].Text);
        Assert.Equal(1, stored.Cause.ConfigurationRevision);
        if (kind == AgentWakeKind.Probability) Assert.True(stored.Cause.Sample >= 0 && stored.Cause.Sample < stored.Cause.Probability);
        else Assert.Null(stored.Cause.Sample);
        Assert.Null(Assert.Single(await ((IAgentHistory)session.Fixture.Runtime.History).GetAgentsAsync(source.Address, wake.TaskId.Value, token)).Assignment);
    }

    /// <summary>未命中条件不调用模型，0% 与关闭开关不能退化成随机唤醒。</summary>
    [Theory]
    [InlineData("""{"wakePrefixes":[],"mentions":false}""", "/agent你好")]
    [InlineData("""{"wakePrefixes":[],"keywords":[]}""", "米粒")]
    [InlineData("""{"wakePrefixes":[],"probability":{"enabled":false,"value":1}}""", "普通")]
    [InlineData("""{"wakePrefixes":[],"probability":{"enabled":true,"value":0}}""", "普通")]
    [InlineData("""{"wakePrefixes":["/Agent"]}""", "/agent你好")]
    [InlineData("""{"keywords":["a.b"]}""", "axb")]
    public async Task UnmatchedMessagesStayOrdinaryChat(string settings, string text)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync(settings, token);
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message(text), token)).Message!;
        await session.Fixture.FinishedAsync(source.Id, token);
        Assert.Equal(0, session.Model.TotalRequests); Assert.Empty(session.Fixture.Transport.Requests);
        Assert.Null(await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(source.Id, token));
    }

    /// <summary>概率覆盖所有有效消息段，卡片与未知载荷的文字不参与关键词匹配。</summary>
    [Theory]
    [InlineData(SegmentTypes.Image)]
    [InlineData(SegmentTypes.Audio)]
    [InlineData(SegmentTypes.Video)]
    [InlineData(SegmentTypes.Json)]
    [InlineData(SegmentTypes.Forward)]
    [InlineData(SegmentTypes.Reply)]
    [InlineData("unrecognized.test")]
    [InlineData("failed.test")]
    [InlineData(SegmentTypes.Text)]
    public async Task ProbabilityCoversEveryAcceptedMessageType(string kind)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("""{"wakePrefixes":[],"mentions":false,"keywords":["隐藏"],"probability":{"enabled":true,"value":1}}""", token);
        IncomingSegment segment = kind == SegmentTypes.Text ? new() { Kind = kind, Text = "  " } : new()
        {
            Kind = kind, Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = "/agent 隐藏", ["target"] = "bot" }),
            ParseState = kind == "unrecognized.test" ? SegmentParseState.Unrecognized : kind == "failed.test" ? SegmentParseState.Invalid : SegmentParseState.Parsed
        };
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message() with { Segments = [segment] }, token)).Message!;
        AgentWakeResult wake = await session.FinishedAsync(source, token);
        AgentEntryPage entries = await ((IAgentHistory)session.Fixture.Runtime.History).ReadEntriesAsync(source.Address, wake.TaskId!.Value, wake.AgentId!.Value, 0, 100, token);
        Assert.Equal(AgentWakeKind.Probability, entries.Entries[0].Data.Deserialize<AgentWakeInput>()!.Cause.Kind);
    }

    /// <summary>关闭提及仅影响该条件；真正的提及仍保留在关键词触发的上下文中。</summary>
    [Fact]
    public async Task MentionSwitchDoesNotEraseContentOrDisableKeywords()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("""{"wakePrefixes":[],"mentions":false,"keywords":["米粒"]}""", token);
        MessageSegment mention = SegmentValues.Mention("bot");
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message() with { Segments =
            [new IncomingSegment { Kind = mention.Kind, Payload = mention.Payload }, new IncomingSegment { Kind = SegmentTypes.Text, Text = "米粒" }] }, token)).Message!;
        AgentWakeResult wake = await session.FinishedAsync(source, token);
        AgentWakeInput stored = (await ((IAgentHistory)session.Fixture.Runtime.History).ReadEntriesAsync(source.Address, wake.TaskId!.Value, wake.AgentId!.Value, 0, 1, token)).Entries[0].Data.Deserialize<AgentWakeInput>()!;
        Assert.Equal(AgentWakeKind.Keyword, stored.Cause.Kind); Assert.Equal(SegmentTypes.Mention, stored.Content[0].Kind);
    }

    /// <summary>固定管理命令即使 Agent 关闭仍可处理，参数错误也不转成概率唤醒。</summary>
    [Theory]
    [InlineData(false, "/agent status")]
    [InlineData(true, "/agent status extra")]
    [InlineData(true, "/agent result not-a-guid")]
    [InlineData(true, "/agent cancel unexpected")]
    public async Task ManagementCommandsAreIndependent(bool enabled, string text)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("""{"wakePrefixes":["!"],"mentions":false,"probability":{"enabled":true,"value":1}}""", token, enabled);
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message(text), token)).Message!;
        await session.Fixture.FinishedAsync(source.Id, token);
        Assert.Single(session.Fixture.Transport.Requests); Assert.Equal(0, session.Model.TotalRequests);
        Assert.Null(await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(source.Id, token));
    }

    /// <summary>无效规则在插件启用和更新时明确拒绝，上一配置修订保持有效。</summary>
    [Theory]
    [InlineData("""{"commands":true}""")]
    [InlineData("""{"wakePrefixes":[""]}""")]
    [InlineData("""{"wakePrefixes":["/","/"]}""")]
    [InlineData("""{"keywords":["A","a"]}""")]
    [InlineData("""{"keywords":["  "]}""")]
    [InlineData("""{"keywords":[7]}""")]
    [InlineData("""{"mentions":"true"}""")]
    [InlineData("""{"mentions":true,"mentions":false}""")]
    [InlineData("""{"probability":{"enabled":true,"value":-0.1}}""")]
    [InlineData("""{"probability":{"enabled":true,"value":1.1}}""")]
    [InlineData("""{"probability":{"enabled":true,"value":"0.5"}}""")]
    [InlineData("""{"probability":{"enabled":true,"value":1e400}}""")]
    [InlineData("""{"probability":{"unknown":true}}""")]
    public async Task InvalidConfigurationKeepsPublishedRevision(string invalid)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("{}", token);
        ConversationConfigurationService profiles = (ConversationConfigurationService)session.Fixture.Runtime.Profiles;
        long revision = profiles.Resolve(TestStore.Address()).Revision;
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.PublishAsync(session.WithSettings(invalid), token));
        Assert.Equal(revision, profiles.Resolve(TestStore.Address()).Revision);
        Assert.Equal(0, session.Model.TotalRequests);
    }

    /// <summary>配置更新仅影响新消息，重复上报不会重新抽取概率。</summary>
    [Fact]
    public async Task RuleReloadDoesNotReplayOldMessage()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("""{"wakePrefixes":[],"probability":{"enabled":true,"value":0}}""", token);
        IncomingMessage original = TestStore.Message("原消息", "before-reload");
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(original, token)).Message!;
        await session.Fixture.FinishedAsync(source.Id, token);
        await ((ConversationConfigurationService)session.Fixture.Runtime.Profiles).PublishAsync(session.WithSettings("""{"wakePrefixes":[],"probability":{"enabled":true,"value":1}}"""), token);
        Assert.True((await session.Fixture.Runtime.ReceiveAsync(original, token)).Duplicate);
        MessageSnapshot next = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("新消息"), token)).Message!;
        await session.FinishedAsync(next, token);
        Assert.Equal(1, session.Model.TotalRequests);
        Assert.Null(await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(source.Id, token));
    }

    /// <summary>管理员新增撤销立即作用于现有运行；关闭 Agent 和全部唤醒规则也保留管理入口。</summary>
    [Fact]
    public async Task CurrentAdministratorsControlDisabledProfileRun()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("{}", token, steps: [new ModelMockStep { WaitForSignal = "blocked", Text = "未发送的结果" }]);
        MessageSnapshot trigger = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent普通聊天") with { SenderId = "first" }, token)).Message!;
        await session.Model.NextRequestAsync(token);
        await session.Fixture.FinishedAsync(trigger.Id, token);
        AgentWakeResult wake = (await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(trigger.Id, token))!;
        ConversationConfiguration disabled = session.WithSettings("""{"wakePrefixes":[],"mentions":false,"allowedConversations":[{"adapterId":"test","platformId":"synthetic","botAccountId":"bot","conversationKind":"group","conversationId":"other"}]}""");
        ConversationConfigurationService profiles = (ConversationConfigurationService)session.Fixture.Runtime.Profiles;
        disabled = disabled with { Profiles = [disabled.Profiles[0] with { Agent = disabled.Profiles[0].Agent with { Enabled = false, Administrators = ["old-admin"] } }] };
        await profiles.PublishAsync(disabled, token);
        MessageSnapshot denied = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent cancel") with { SenderId = "first" }, token)).Message!;
        await session.Fixture.FinishedAsync(denied.Id, token);
        Assert.NotNull(await session.Fixture.Runtime.Agents.GetActiveAsync(trigger.Address, token));
        disabled = disabled with { Profiles = [disabled.Profiles[0] with { Agent = disabled.Profiles[0].Agent with { Administrators = ["new-admin"] } }] };
        await profiles.PublishAsync(disabled, token);
        MessageSnapshot revoked = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent cancel") with { SenderId = "old-admin" }, token)).Message!;
        await session.Fixture.FinishedAsync(revoked.Id, token);
        Assert.NotNull(await session.Fixture.Runtime.Agents.GetActiveAsync(trigger.Address, token));
        MessageSnapshot stop = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent cancel") with { SenderId = "new-admin" }, token)).Message!;
        await session.Fixture.FinishedAsync(stop.Id, token);
        await session.FinishedAsync(trigger, token);
        AgentTaskSnapshot ended = (await ((IAgentHistory)session.Fixture.Runtime.History).GetTaskAsync(trigger.Address, wake.TaskId!.Value, token))!;
        Assert.Equal(AgentTaskState.Cancelled, ended.State);
        Assert.Equal(1, session.Model.TotalRequests);
        Assert.Equal(3, session.Fixture.Transport.Requests.Count);
        OutgoingMessage[] replies = session.Fixture.Transport.Requests.ToArray();
        Assert.Contains("只有当前配置", replies[0].Segments[0].Text);
        Assert.Contains("只有当前配置", replies[1].Segments[0].Text);
        Assert.Contains("已请求停止", replies[2].Segments[0].Text);
    }

    /// <summary>100% 不能越过自身身份或完整会话白名单；不同账号、适配器与群仍彼此隔离。</summary>
    [Theory]
    [InlineData("self")]
    [InlineData("outgoing")]
    [InlineData("adapter")]
    [InlineData("account")]
    [InlineData("conversation")]
    public async Task ProbabilityRespectsIdentityAndScope(string difference)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string settings = JsonSerializer.Serialize(new Dictionary<string, object>
        { ["wakePrefixes"] = Array.Empty<string>(), ["probability"] = new Dictionary<string, object> { ["enabled"] = true, ["value"] = 1 }, ["allowedConversations"] = new[] { TestStore.Address() } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await using Session session = await Session.CreateAsync(settings, token);
        IncomingMessage incoming = TestStore.Message("普通消息");
        incoming = difference switch
        {
            "self" => incoming with { SenderId = incoming.Address.BotAccountId },
            "outgoing" => incoming with { Direction = MessageDirection.Outgoing },
            "adapter" => incoming with { Address = incoming.Address with { AdapterId = "another" } },
            "account" => incoming with { Address = incoming.Address with { BotAccountId = "another" } },
            _ => incoming with { Address = incoming.Address with { ConversationId = "another" } }
        };
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(incoming, token)).Message!;
        await session.Fixture.FinishedAsync(source.Id, token);
        Assert.Null(await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(source.Id, token));
        Assert.Equal(0, session.Model.TotalRequests); Assert.Empty(session.Fixture.Transport.Requests);
    }

    /// <summary>已登记命令在斜杠前缀及 100% 概率之前接管；其他插件仍使用自己的用途。</summary>
    [Fact]
    public async Task RegisteredCommandPrecedesSlashWake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("""{"wakePrefixes":["/"],"probability":{"enabled":true,"value":1}}""", token);
        string package = await session.Fixture.PackageAsync("command", "v1", token);
        await session.Fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token);
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/history"), token)).Message!;
        await session.Fixture.FinishedAsync(source.Id, token);
        Assert.Equal("command:v1", Assert.Single(session.Fixture.Transport.Requests).Segments[0].Text);
        Assert.Equal(0, session.Model.TotalRequests);
        Assert.Null(await ((IAgentStore)session.Fixture.Runtime.History).FindWakeAsync(source.Id, token));
    }

    /// <summary>消息处理积压期间窗口已淘汰唤醒源，首次模型请求也不能从唤醒审计或根描述回填。</summary>
    [Fact]
    public async Task EvictedWakeNeverAppearsInFirstDecision()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("{}", token);
        ConversationConfiguration configuration = session.WithSettings("{}");
        await ((ConversationConfigurationService)session.Fixture.Runtime.Profiles).PublishAsync(configuration with
        { Profiles = [configuration.Profiles[0] with { MaximumMessages = 4, DiscardMessages = 2 }] }, token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int holds = 0;
        session.Fixture.Transport.OnSend = async (id, message, cancellation) =>
        {
            if (message.Segments[0].Text == "hold" && Interlocked.Increment(ref holds) == 1)
            { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
            return new TransportResult { State = DeliveryState.Confirmed };
        };
        string package = await session.Fixture.PackageAsync("backlog", "v1", token);
        await session.Fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("wait"), token);
        try
        {
            MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent绝不回填的旧唤醒"), token)).Message!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            for (int index = 0; index < 6; index++) await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("积压聊天-" + index), token);
            ConversationWindow window = await ((IModelStore)session.Fixture.Runtime.History).CaptureWindowAsync(source.Address, 4, 2, token);
            Assert.DoesNotContain(window.Messages, message => message.Id == source.Id);
            release.TrySetResult();
            await session.FinishedAsync(source, token);
            JsonElement request = Assert.Single(session.Model.Requests).Body!.Value;
            string text = string.Join("\n", request.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").ValueKind == JsonValueKind.String
                ? message.GetProperty("content").GetString() : string.Concat(message.GetProperty("content").EnumerateArray().Select(block => block.GetProperty("text").GetString()))));
            Assert.DoesNotContain("绝不回填", text);
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>在途模型的运行事实与发言行为使用同一轮次输出模式，热更新不会改变该轮发送语义。</summary>
    [Theory]
    [InlineData(AgentOutputMode.Direct)]
    [InlineData(AgentOutputMode.ToolsOnly)]
    public async Task OutputModeIsFrozenForCurrentTurn(AgentOutputMode initial)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Session session = await Session.CreateAsync("{}", token, steps: [new ModelMockStep { WaitForSignal = "blocked", Text = "本轮结果" }]);
        ConversationConfiguration configuration = session.WithSettings("{}");
        ConversationConfigurationService profiles = (ConversationConfigurationService)session.Fixture.Runtime.Profiles;
        configuration = configuration with { Profiles = [configuration.Profiles[0] with { Agent = configuration.Profiles[0].Agent with { OutputMode = initial } }] };
        await profiles.PublishAsync(configuration, token);
        MessageSnapshot source = (await session.Fixture.Runtime.ReceiveAsync(TestStore.Message("/agent聊天"), token)).Message!;
        ModelMockRequest request = await session.Model.NextRequestAsync(token);
        await profiles.PublishAsync(configuration with { Profiles = [configuration.Profiles[0] with { Agent = configuration.Profiles[0].Agent with
        { OutputMode = initial == AgentOutputMode.Direct ? AgentOutputMode.ToolsOnly : AgentOutputMode.Direct } }] }, token);
        session.Model.Release("blocked");
        await session.FinishedAsync(source, token);
        Assert.Equal(initial == AgentOutputMode.Direct ? 1 : 0, session.Fixture.Transport.Requests.Count);
        Assert.Contains(initial == AgentOutputMode.Direct ? "direct" : "tools_only", request.Body!.Value.GetRawText());
    }

    /// <summary>独占真实宿主与模型 Mock，配置只包含合成身份和环境变量。</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly string _variable;
        private readonly ConversationConfiguration _configuration;
        public RuntimeFixture Fixture { get; }
        public ModelMockServer Model { get; }

        /// <summary>持有本场景资源和已发布配置，退出时清理合成凭据。</summary>
        private Session(RuntimeFixture fixture, ModelMockServer model, string variable, ConversationConfiguration configuration)
        { Fixture = fixture; Model = model; _variable = variable; _configuration = configuration; }

        /// <summary>使用实际提供商和入口包，测试不会将插件实现加载到默认上下文。</summary>
        public static async Task<Session> CreateAsync(string settings, CancellationToken token, bool enabled = true, ImmutableArray<ModelMockStep> steps = default)
        {
            ModelMockServer model = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true, Steps = steps.IsDefault ? [new ModelMockStep { Text = "wake-complete" }] : steps }, "http://127.0.0.1:0", token);
            string variable = "MISSILI_WAKE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
            ConversationConfiguration configuration = ModelFailureTests.Configuration(model.BaseAddress, variable, ModelProtocol.ChatCompletions);
            using JsonDocument json = JsonDocument.Parse(settings);
            configuration = configuration with { Models = configuration.Models.Select(item => item with { Tools = true }).ToImmutableArray(), Profiles = [configuration.Profiles[0] with
            { Agent = new AgentProfile { Enabled = enabled, Models = ["A"] }, Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add(PluginId, json.RootElement.Clone()) }] };
            RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            PluginStatus loaded = await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("agent-chat"), RuntimeFixture.Configuration(), token);
            Assert.Equal(PluginState.Active, loaded.State); fixture.Runtime.OpenAgents();
            return new Session(fixture, model, variable, configuration);
        }

        /// <summary>只替换入口命名空间，用生产配置发布路径校验变更。</summary>
        public ConversationConfiguration WithSettings(string settings)
        {
            using JsonDocument json = JsonDocument.Parse(settings);
            return _configuration with { Profiles = [_configuration.Profiles[0] with { Plugins = _configuration.Profiles[0].Plugins.SetItem(PluginId, json.RootElement.Clone()) }] };
        }

        /// <summary>以持久化释放标记判断运行结束，不把监听器返回或固定延迟当作成功。</summary>
        public async Task<AgentWakeResult> FinishedAsync(MessageSnapshot source, CancellationToken token)
        {
            await Fixture.FinishedAsync(source.Id, token);
            AgentWakeResult wake = (await ((IAgentStore)Fixture.Runtime.History).FindWakeAsync(source.Id, token))!;
            Assert.NotNull(wake);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while ((await ((IAgentHistory)Fixture.Runtime.History).GetTaskAsync(source.Address, wake.TaskId!.Value, deadline.Token))!.ReleasedAt is null) await Task.Delay(10, deadline.Token);
            return wake;
        }

        /// <summary>回收真实插件、连接和临时数据库，不留下继续运行的测试任务。</summary>
        public async ValueTask DisposeAsync()
        {
            Model.Release("blocked");
            try { await Fixture.DisposeAsync(); }
            finally { await Model.DisposeAsync(); Environment.SetEnvironmentVariable(_variable, null); }
        }
    }
}
