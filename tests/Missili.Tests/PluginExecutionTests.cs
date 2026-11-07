using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;
using Missili.Host;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实宿主绑定、插件包和执行链验证统一额度，不以配置对象本身代替运行验收。</summary>
public sealed class PluginExecutionTests
{
    /// <summary>宿主配置省略时采用两组默认额度，且不会提前创建运行存储。</summary>
    [Fact]
    public async Task MissingSectionUsesDefaultsBeforeResourcesStart()
    {
        string root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        await using WebApplication app = Build(root, []);
        PluginExecutionOptions options = app.Services.GetRequiredService<PluginExecutionOptions>();
        Assert.Equal(4, options.PluginConcurrencyLimit);
        Assert.Equal(32, options.Handlers.Concurrency); Assert.Equal(512, options.Handlers.QueueCapacity);
        Assert.Equal(8, options.ToolsAndProcessors.Concurrency); Assert.Equal(2, options.ToolsAndProcessors.PerPluginConcurrency);
        Assert.Equal(128, options.ToolsAndProcessors.QueueCapacity);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>旧配置和拼错字段必须拒绝，不能被绑定器忽略后悄悄恢复默认额度。</summary>
    [Theory]
    [InlineData("Infrastructure:PluginConcurrency")]
    [InlineData("Infrastructure:PerPluginConcurrency")]
    [InlineData("Infrastructure:InvocationCapacity")]
    [InlineData("Agents:ToolConcurrency")]
    [InlineData("Agents:PerPluginToolConcurrency")]
    [InlineData("Agents:ToolQueueCapacity")]
    [InlineData("Agents:SubmissionCapacity")]
    [InlineData("Agents:SubmissionBytes")]
    [InlineData("PluginExecution:Handlers:Concurency")]
    public void OldOrUnknownFieldsFailBeforeCreatingRuntime(string key)
    {
        string root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => Build(root, new Dictionary<string, string?> { [key] = "1" }));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>非法额度在注册后台资源前失败；清单上限与两种通道各自校验。</summary>
    [Theory]
    [InlineData("PluginExecution:PluginConcurrencyLimit", "0")]
    [InlineData("PluginExecution:PluginConcurrencyLimit", "33")]
    [InlineData("PluginExecution:Handlers:Concurrency", "0")]
    [InlineData("PluginExecution:Handlers:QueueCapacity", "-1")]
    [InlineData("PluginExecution:ToolsAndProcessors:Concurrency", "0")]
    [InlineData("PluginExecution:ToolsAndProcessors:PerPluginConcurrency", "9")]
    [InlineData("PluginExecution:ToolsAndProcessors:QueueCapacity", "0")]
    public void InvalidQuotasFailBeforeCreatingRuntime(string key, string value)
    {
        string root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => Build(root, new Dictionary<string, string?> { [key] = value }));
        Assert.False(Directory.Exists(root));
    }

    /// <summary>Agent 关闭时，同插件消息回调持有唯一消息槽，出站处理仍能在独立通道完成。</summary>
    [Fact]
    public async Task DisabledAgentDoesNotDisableProcessorOrShareHandlerSlot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration configuration = new() { DefaultProfileId = "default", Profiles = [new ConversationProfile { Id = "default", Outgoing = Processing() }] };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, pluginExecutionOptions: SingleSlots());
        PluginStatus plugin = await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("probe", "v1", token), RuntimeFixture.Configuration("execution-probe-handler"), token);
        Assert.Equal(PluginState.Active, plugin.State);
        MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message(), token)).Message!;
        await fixture.FinishedAsync(source.Id, token).WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.Equal("handler:processed", string.Concat(Assert.Single(fixture.Transport.Requests).Segments.Select(segment => segment.Text)));
        Assert.Null(await fixture.Runtime.Agents.GetActiveAsync(source.Address, token));
        Assert.Empty((await fixture.Runtime.Models.Calls.QueryCallsAsync(new ModelCallQuery(), token)).Calls);
    }

    /// <summary>工具与出站处理共用单槽时，宿主先释放生成工具槽再运行同插件处理器。</summary>
    [Fact]
    public async Task SamePluginToolDraftReleasesSharedSlotBeforeProcessing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument response = JsonDocument.Parse("""{"id":"probe","object":"chat.completion","created":1,"model":"A","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"one","type":"function","function":{"name":"probe_generate","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { StrictSteps = true,
            Steps = [new ModelMockStep { Response = response.RootElement.Clone() }, new ModelMockStep { Text = "内部结束" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_EXECUTION_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions);
            configuration = configuration with { Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Outgoing = Processing(), Agent = new AgentProfile { Enabled = true, Models = ["A"], Stream = false,
                    Tools = ["probe.generate"], OutputMode = AgentOutputMode.ToolsOnly } }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true }, pluginExecutionOptions: SingleSlots());
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("probe", "v1", token), RuntimeFixture.Configuration("execution-probe-tool"), token);
            fixture.Runtime.OpenAgents();
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message("合成唤醒"), token)).Message!;
            AgentWakeResult wake = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { MessageId = source.Id, CurrentContent = source.Segments, Cause = TestStore.WakeCause }, token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            AgentTaskSnapshot task;
            do { task = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(source.Address, wake.TaskId!.Value, deadline.Token))!;
                if (task.ReleasedAt is null) await Task.Delay(10, deadline.Token); } while (task.ReleasedAt is null);
            Assert.Equal(AgentTaskState.Completed, task.State); Assert.Equal(1, task.Usage.ToolCalls); Assert.Equal(2, mock.TotalRequests);
            Assert.Equal("tool:processed", string.Concat(Assert.Single(fixture.Transport.Requests).Segments.Select(segment => segment.Text)));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>清单声明上限只来自宿主配置，省略、缩小和越界分别通过实际包加载验证。</summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    public async Task ManifestLimitUsesHostConfiguration(int? requested, int expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, pluginExecutionOptions: new PluginExecutionOptions
        { PluginConcurrencyLimit = 2, Handlers = new PluginHandlerExecutionOptions { Concurrency = 2, QueueCapacity = 1 } });
        string package = await fixture.PackageAsync("limit", "v1", token, requested);
        if (expected == 0) await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token));
        else
        {
            await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token);
            Assert.All(fixture.Runtime.Handlers.Capture(MessageHooks.Received, HandlerKind.Listener), handler => Assert.Equal(expected, handler.MaxConcurrency));
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message(), token)).Message!;
            await fixture.FinishedAsync(source.Id, token); Assert.Single(fixture.Transport.Requests);
        }
    }

    /// <summary>隔离宿主配置来源，不让本机配置影响字段拒绝及默认值验收。</summary>
    private static WebApplication Build(string root, Dictionary<string, string?> configuration) => Missili.Host.Program.BuildApplication([], builder =>
    {
        builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Infrastructure:DataDirectory"] = root });
    });

    /// <summary>两类通道都只有一个执行槽，清单也只能声明单并发。</summary>
    private static PluginExecutionOptions SingleSlots() => new() { PluginConcurrencyLimit = 1,
        Handlers = new PluginHandlerExecutionOptions { Concurrency = 1, QueueCapacity = 1 },
        ToolsAndProcessors = new PluginToolProcessorExecutionOptions { Concurrency = 1, PerPluginConcurrency = 1, QueueCapacity = 1 } };

    /// <summary>明确使用同一真实插件提供的处理器。</summary>
    private static OutgoingOptions Processing() => new() { Processors = [new HandlerKey { PluginId = "probe", HandlerId = "probe" }] };
}
