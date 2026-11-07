using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Nexus;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 DLL 与私有依赖的加载、替换和实际回收验收。</summary>
public sealed class PluginTests
{
    /// <summary>契约 4 在实例化前明确拒载，不发布处理器或尝试旧源码适配。</summary>
    [Fact]
    public async Task PreviousContractIsRejectedBeforeLoading()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("old-contract", "v1", token);
        string path = Path.Combine(package, "plugin.json");
        System.Text.Json.Nodes.JsonObject manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        manifest["contractVersion"] = 4;
        await File.WriteAllTextAsync(path, manifest.ToJsonString(), token);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token));
        Assert.Contains("契约版本", error.Message);
        Assert.Empty(fixture.Runtime.Plugins.GetStatuses());
    }

    /// <summary>本地 ZIP 使用实际安装和加载流程，越界包在写入前拒绝。</summary>
    [Fact]
    public async Task ZipInstallationUsesRealPackageAndRejectsTraversal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("sample", "v1", token);
        string zip = Path.Combine(fixture.Root, "plugin.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(package, zip);
        string installed = await fixture.Runtime.Plugins.InstallAsync(zip, token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(installed, RuntimeFixture.Configuration(), token)).State);
        string invalid = Path.Combine(fixture.Root, "invalid.zip");
        using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(invalid, System.IO.Compression.ZipArchiveMode.Create))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("../escaped.txt");
            await using Stream content = entry.Open();
            await content.WriteAsync(new byte[] { 1 }, token);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runtime.Plugins.InstallAsync(invalid, token));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "plugins", "escaped.txt")));
    }

    /// <summary>重载取消正在执行和排队中的旧代次调用，不让旧快照调用新版本。</summary>
    [Fact]
    public async Task StopCancelsQueuedAndRunningCallsWithoutReplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        fixture.Transport.OnSend = async (_, message, cancellation) =>
        {
            if (message.Segments[0].Text == "hold") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new TransportResult { State = DeliveryState.Confirmed };
        };
        string package = await fixture.PackageAsync("sample", "v1", token, concurrency: 1);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("wait"), token);
        ReceiveReceipt first = await fixture.Runtime.ReceiveAsync(TestStore.Message("first"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "hold", token);
        ReceiveReceipt second = await fixture.Runtime.ReceiveAsync(TestStore.Message("second"), token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (fixture.Runtime.Plugins.GetStatuses().Single().ActiveCalls < 2) await Task.Delay(1, deadline.Token);
        Assert.Equal(PluginState.Stopped, (await fixture.Runtime.Plugins.StopAsync("sample", token)).State);
        await fixture.FinishedAsync(first.Message!.Id, token);
        await fixture.FinishedAsync(second.Message!.Id, token);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>更新代码与私有依赖，同时保留稳定处理器的用户顺序。</summary>
    [Fact]
    public async Task ReloadSwitchesRealAssembliesAndPreservesOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string first = await fixture.PackageAsync("sample", "v1", token);
        string second = await fixture.PackageAsync("sample", "v2", token);
        await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration(), token);
        HookOrderView order = await fixture.Runtime.Orders.GetAsync(MessageHooks.Received, HandlerKind.Interceptor, token);
        await fixture.Runtime.Orders.SaveAsync(order.HookId, order.Kind, [order.Items[0].Handler.Key], order.Revision, token);
        ReceiveReceipt input = await fixture.Runtime.ReceiveAsync(TestStore.Message("first"), token);
        await fixture.FinishedAsync(input.Message!.Id, token);
        Assert.Equal("v1:v1>first", Assert.Single(fixture.Transport.Requests).Segments[0].Text);
        PluginStatus reloaded = await fixture.Runtime.Plugins.ReloadAsync("sample", second, RuntimeFixture.Configuration(), token);
        Assert.Equal(PluginState.Active, reloaded.State);
        ReceiveReceipt next = await fixture.Runtime.ReceiveAsync(TestStore.Message("second"), token);
        await fixture.FinishedAsync(next.Message!.Id, token);
        Assert.Equal("v2:v2>second", fixture.Transport.Requests.Last().Segments[0].Text);
        Assert.True((await fixture.Runtime.Orders.GetAsync(order.HookId, order.Kind, token)).Customized);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name is "Fixture.Plugin" or "Fixture.PrivateDependency");
    }

    /// <summary>停止登记后台任务及资源后，旧上下文必须真正可回收。</summary>
    [Fact]
    public async Task StoppedGenerationCanBeCollectedAndResourcesAreReleased()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string marker = Path.Combine(fixture.Root, "released.txt");
        WeakReference reference = await LoadAndStopAsync(fixture, marker, token);
        // GC 只用于卸载验收，不属于生产消息处理流程。
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Assert.False(reference.IsAlive);
        Assert.Equal("released", await File.ReadAllTextAsync(marker, token));
    }

    /// <summary>移出产生程序集引用的调用栈，弱引用测试只保留普通值。
    /// 依据：https://learn.microsoft.com/dotnet/standard/assembly/unloadability 。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndStopAsync(RuntimeFixture fixture, string marker, CancellationToken token)
    {
        string package = await fixture.PackageAsync("sample", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("background", marker), token);
        ReceiveReceipt input = await fixture.Runtime.ReceiveAsync(TestStore.Message(), token);
        await fixture.FinishedAsync(input.Message!.Id, token);
        Assert.Equal(PluginState.Stopped, (await fixture.Runtime.Plugins.StopAsync("sample", token)).State);
        return ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("sample")!;
    }

    /// <summary>新版本初始化失败时不恢复旧版本，也不发布部分处理器。</summary>
    [Fact]
    public async Task InitializationFailureDoesNotRollbackOrPublishPartialHandlers()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string first = await fixture.PackageAsync("sample", "v1", token);
        string second = await fixture.PackageAsync("sample", "v2", token);
        await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration(), token);
        PluginStatus result = await fixture.Runtime.Plugins.ReloadAsync("sample", second, RuntimeFixture.Configuration("init-fail"), token);
        Assert.Equal(PluginState.Faulted, result.State);
        ReceiveReceipt input = await fixture.Runtime.ReceiveAsync(TestStore.Message(), token);
        await fixture.FinishedAsync(input.Message!.Id, token);
        Assert.Empty(fixture.Transport.Requests);
        Assert.All(fixture.Runtime.Handlers.Capture(MessageHooks.Received, HandlerKind.Listener), handler => Assert.False(handler.Enabled));
    }

    /// <summary>忽略取消时，默认三十秒到期仍保留旧调用且不创建新代次。</summary>
    [Fact]
    public async Task UncooperativeInvocationPreventsReloadAfterDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.OnSend = async (id, message, cancellation) =>
        {
            if (message.Segments[0].Text == "hold") await release.Task;
            return new TransportResult { State = DeliveryState.Confirmed };
        };
        string first = await fixture.PackageAsync("sample", "v1", token);
        string second = await fixture.PackageAsync("sample", "v2", token);
        PluginStatus original = await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration("ignore-cancellation"), token);
        ReceiveReceipt input = await fixture.Runtime.ReceiveAsync(TestStore.Message(), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "hold", token);
        Task<PluginStatus> reload = fixture.Runtime.Plugins.ReloadAsync("sample", second, RuntimeFixture.Configuration(), token);
        // 包读取是异步的，先等到停止阶段再推进可控时钟。
        while (fixture.Runtime.Plugins.GetStatuses().Single().State != PluginState.Stopping) await Task.Delay(1, token);
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            // 连续推进可控时钟直到实际停止任务结束，不假设计时器与状态发布同步完成。
            while (!reload.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1, deadline.Token);
            }
            PluginStatus timedOut = await reload.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(PluginState.StopTimedOut, timedOut.State);
            Assert.Equal(original.Generation, timedOut.Generation);
            Assert.True(timedOut.ActiveCalls > 0);
        }
        finally { release.TrySetResult(); }
        await fixture.FinishedAsync(input.Message!.Id, token);
        PluginStatus stopped = await fixture.Runtime.Plugins.StopAsync("sample", token);
        Assert.Equal(PluginState.Stopped, stopped.State);
    }
}
