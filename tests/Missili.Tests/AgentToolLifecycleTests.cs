using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Nexus;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>工具来自实际可回收 DLL，测试程序集不引用工具实现或其私有依赖。</summary>
public sealed class AgentToolLifecycleTests
{
    /// <summary>参数准备和执行绑定同一代次，重载后旧描述不可调用，结果保留真实版本。</summary>
    [Fact]
    public async Task RealToolReloadRejectsOldDescriptorAndCollectsGeneration()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token,
            models: ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1")));
        string first = await fixture.PackageAsync("tool", "v1", token);
        string second = await fixture.PackageAsync("tool", "v2", token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration("tool"), token)).State);
        AgentToolDescriptor descriptor = Assert.Single(fixture.Runtime.Tools.CaptureTools());
        ToolExecutionResult result = await InvokeAsync(fixture, descriptor, token);
        Assert.Equal("v1", result.Data!.Value.GetProperty("version").GetString());
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("tool", second, RuntimeFixture.Configuration("tool"), token)).State);
        Assert.Null(fixture.Runtime.Tools.TryAcquireTool(descriptor));
        AgentToolDescriptor updated = Assert.Single(fixture.Runtime.Tools.CaptureTools());
        Assert.NotEqual(descriptor.Generation, updated.Generation);
        Assert.Equal("v2", (await InvokeAsync(fixture, updated, token)).Data!.Value.GetProperty("version").GetString());
        WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("tool")!;
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
        await fixture.Runtime.Plugins.StopAsync("tool", token);
        Assert.False(Assert.Single(fixture.Runtime.Tools.CaptureTools()).Enabled);
    }

    /// <summary>初始化失败及全局命名冲突不发布半份工具注册。</summary>
    [Fact]
    public async Task FailedAndConflictingRegistrationsLeaveActiveToolIntact()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string first = await fixture.PackageAsync("first", "v1", token);
        string conflict = await fixture.PackageAsync("conflict", "v2", token);
        string failed = await fixture.PackageAsync("failed", "v1", token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration("tool"), token)).State);
        Assert.Equal(PluginState.Faulted, (await fixture.Runtime.Plugins.LoadAsync(conflict, RuntimeFixture.Configuration("tool"), token)).State);
        Assert.Equal(PluginState.Faulted, (await fixture.Runtime.Plugins.LoadAsync(failed, RuntimeFixture.Configuration("tool-init-fail"), token)).State);
        AgentToolDescriptor descriptor = Assert.Single(fixture.Runtime.Tools.CaptureTools());
        Assert.Equal("first", descriptor.PluginId);
        Assert.True(descriptor.Enabled);
    }

    /// <summary>忽略取消的实际调用阻止新代次启动，停止超时不提前释放调用资格。</summary>
    [Fact]
    public async Task UncooperativeToolBlocksReloadUntilItsActualExit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { Steps = [new ModelMockStep { WaitForSignal = "release" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_TOOL_LIFETIME_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock,
                models: ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            string first = await fixture.PackageAsync("tool", "v1", token);
            string second = await fixture.PackageAsync("tool", "v2", token);
            await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration("tool-ignore-cancellation"), token);
            AgentToolDescriptor descriptor = Assert.Single(fixture.Runtime.Tools.CaptureTools());
            Task<ToolExecutionResult> work = InvokeAsync(fixture, descriptor, token);
            await mock.NextRequestAsync(token);
            Task<PluginStatus> reload = fixture.Runtime.Plugins.ReloadAsync("tool", second, RuntimeFixture.Configuration("tool"), token);
            try
            {
                while (fixture.Runtime.Plugins.GetStatuses().Single(status => status.Id == "tool").State != PluginState.Stopping) await Task.Delay(1, token);
                clock.Advance(TimeSpan.FromSeconds(31));
                PluginStatus stopped = await reload.WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.Equal(PluginState.StopTimedOut, stopped.State);
                Assert.Equal(descriptor.Generation, stopped.Generation);
                Assert.True(stopped.ActiveCalls > 0);
                Assert.Null(fixture.Runtime.Tools.TryAcquireTool(descriptor));
            }
            finally { mock.Release("release"); await work.WaitAsync(TimeSpan.FromSeconds(10), token); }
            Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("tool", second, RuntimeFixture.Configuration("tool"), token)).State);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>资格在独立调用栈释放，长寿命测试栈只保留共享结果。</summary>
    private static async Task<ToolExecutionResult> InvokeAsync(RuntimeFixture fixture, AgentToolDescriptor descriptor, CancellationToken token)
    {
        AgentToolContext context = new()
        {
            Origin = new OperationOrigin { PluginId = descriptor.PluginId },
            Conversation = TestStore.Address(),
            Profile = fixture.Runtime.Profiles.Resolve(TestStore.Address()),
            Models = fixture.Runtime.Models,
            History = fixture.Runtime.History,
            AgentHistory = (IAgentHistory)fixture.Runtime.History,
            ToolHistory = (IAgentToolHistory)fixture.Runtime.History,
            Search = (IHistorySearch)fixture.Runtime.History,
            Assets = (IConversationAssetAccess)fixture.Runtime.History,
            Contents = fixture.Runtime.Contents,
            Media = fixture.Runtime.Media,
            Transcriptions = fixture.Runtime.Models.Transcriptions,
            Clock = TimeProvider.System
        };
        using IAgentToolLease lease = fixture.Runtime.Tools.TryAcquireTool(descriptor)!;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
        using JsonDocument invalid = JsonDocument.Parse("""{"value":"42"}""");
        PreparedToolCall rejected = await lease.PrepareAsync(context, invalid.RootElement, linked.Token);
        Assert.False(rejected.Valid);
        Assert.Equal("$.value", rejected.ErrorPath);
        using JsonDocument arguments = JsonDocument.Parse("""{"value":42}""");
        PreparedToolCall prepared = await lease.PrepareAsync(context, arguments.RootElement, linked.Token);
        Assert.True(prepared.Valid, prepared.Error);
        return await lease.ExecuteAsync(context, prepared, linked.Token);
    }
}
