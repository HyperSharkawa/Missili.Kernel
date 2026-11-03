using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Nexus;
using Xunit;

namespace Missili.Tests;

/// <summary>适配器目录和归档通过实际编译插件验证，测试程序集不静态引用驱动。</summary>
public sealed class AdapterInfrastructureTests
{
    /// <summary>切换驱动代码后旧程序集真正可回收，初始化失败不发布注册。</summary>
    [Fact]
    public async Task AdapterRegistrationUsesActualCollectibleGeneration()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        WeakReference reference = await ReloadAsync(fixture, token);
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
        string failed = await fixture.PackageAsync("failed", "v1", token);
        Assert.Equal(PluginState.Faulted, (await fixture.Runtime.Plugins.LoadAsync(failed, RuntimeFixture.Configuration("adapter-init-fail"), token)).State);
        Assert.Single(fixture.Runtime.Adapters.GetStatuses());
        string conflict = await fixture.PackageAsync("conflict", "v1", token);
        Assert.Equal(PluginState.Faulted, (await fixture.Runtime.Plugins.LoadAsync(conflict, RuntimeFixture.Configuration("adapter-with-handler"), token)).State);
        Assert.DoesNotContain(((IPluginRuntime)fixture.Runtime.Plugins).Capture(MessageHooks.Received, HandlerKind.Command), item => item.Key.PluginId == "conflict");
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Runtime.Adapters.InvokeAsync("fixture-adapter", "bot", "send_msg_async", JsonSerializer.SerializeToElement(new { }), token));
    }

    /// <summary>在独立调用栈中执行真实版本切换，只返回弱引用。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReloadAsync(RuntimeFixture fixture, CancellationToken token)
    {
        string first = await fixture.PackageAsync("adapter", "v1", token);
        string second = await fixture.PackageAsync("adapter", "v2", token);
        await fixture.Runtime.Plugins.LoadAsync(first, RuntimeFixture.Configuration("adapter"), token);
        Assert.Equal("v1", (await fixture.Runtime.Adapters.InvokeAsync("fixture-adapter", "bot", "version", JsonSerializer.SerializeToElement(new { }), token)).Data!.Value.GetProperty("version").GetString());
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("adapter", second, RuntimeFixture.Configuration("adapter"), token)).State);
        Assert.Equal("v2", (await fixture.Runtime.Adapters.InvokeAsync("fixture-adapter", "bot", "version", JsonSerializer.SerializeToElement(new { }), token)).Data!.Value.GetProperty("version").GetString());
        return ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("adapter")!;
    }

    /// <summary>仍未释放的解析流阻止卸载，超时后不会启用第二个代次。</summary>
    [Fact]
    public async Task OpenPluginStreamRetainsGenerationUntilDisposed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock);
        string package = await fixture.PackageAsync("adapter", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("adapter"), token);
        await using MediaSourceRead read = await fixture.Runtime.Adapters.ResolveMediaAsync(Source(), token);
        Task<PluginStatus> stopping = fixture.Runtime.Plugins.StopAsync("adapter", token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!stopping.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1, deadline.Token); }
        Assert.Equal(PluginState.StopTimedOut, (await stopping).State);
        Assert.Empty(fixture.Runtime.Adapters.GetStatuses());
        await read.DisposeAsync();
        Assert.Equal(PluginState.Stopped, (await fixture.Runtime.Plugins.StopAsync("adapter", token)).State);
    }

    /// <summary>未加载适配器时保留归档，加载后恢复节点与媒体但不触发聊天回复。</summary>
    [Fact]
    public async Task ArchivesResumeAfterAdapterAppearsAndKeepStableMediaPositions()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        MessageAddress address = TestStore.Address() with { AdapterId = "fixture-adapter" };
        Guid contentId = await fixture.Runtime.Contents.RequestContentAsync(new ContentSource { Kind = "forward", Address = address, PlatformId = "f1" }, token);
        ReceiveReceipt received = await fixture.Runtime.ReceiveAsync(TestStore.Message() with
        { Address = address, Segments = [new IncomingSegment { Kind = SegmentTypes.Image, Media = [new IncomingMedia { Kind = MediaKind.Image, Source = Source() }] }] }, token);
        Guid mediaId = received.Message!.Segments[0].MediaIds[0];
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await fixture.Runtime.Contents.GetContentAsync(contentId, deadline.Token))!.State != ContentState.WaitingForAdapter ||
               (await fixture.Runtime.Media.GetStatusAsync(mediaId, deadline.Token))!.State != MediaState.WaitingForAdapter)
            await Task.Delay(1, deadline.Token);
        string package = await fixture.PackageAsync("adapter", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("adapter"), token);
        StructuredContentSnapshot content = await fixture.Runtime.Contents.WaitContentAsync(contentId, deadline.Token);
        Assert.Equal(ContentState.Complete, content.State);
        ContentNode node = Assert.Single(content.Nodes);
        Guid archivedMediaId = Assert.Single(node.Segments[1].MediaIds);
        await using MediaReadResult media = await fixture.Runtime.Media.OpenReadAsync(mediaId, deadline.Token);
        await using MediaReadResult nested = await fixture.Runtime.Media.OpenReadAsync(archivedMediaId, deadline.Token);
        Assert.Equal(MediaState.Available, nested.Status.State);
        Assert.Equal(media.Status.ContentKey, nested.Status.ContentKey);
        Assert.Equal(contentId, await fixture.Runtime.Contents.RequestContentAsync(content.Source, token));
        Assert.Equal(archivedMediaId, (await fixture.Runtime.Contents.GetContentAsync(contentId, token))!.Nodes[0].Segments[1].MediaIds[0]);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>导入原件与平台获取共用内容去重，返回流相互独立。</summary>
    [Fact]
    public async Task ImportedMediaCanBeReadAndSentWithoutInboundMessage()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await using MemoryStream first = new(Encoding.UTF8.GetBytes("synthetic-media"));
        await using MemoryStream second = new(Encoding.UTF8.GetBytes("synthetic-media"));
        MediaStatus left = await fixture.Runtime.MediaImporter.ImportAsync(MediaKind.Image, first, cancellationToken: token);
        MediaStatus right = await fixture.Runtime.MediaImporter.ImportAsync(MediaKind.Image, second, cancellationToken: token);
        Assert.NotEqual(left.Id, right.Id);
        Assert.Equal(left.ContentKey, right.ContentKey);
        await using MediaReadResult a = await fixture.Runtime.Media.OpenReadAsync(left.Id, token);
        await using MediaReadResult b = await fixture.Runtime.Media.OpenReadAsync(right.Id, token);
        Assert.Equal((int)'s', a.Content!.ReadByte());
        Assert.Equal(0, b.Content!.Position);
        SendResult sent = await fixture.Runtime.Sender.SendAsync(new OutgoingMessage
        { Address = TestStore.Address(), Segments = [new MessageSegment { Kind = SegmentTypes.Image, MediaIds = [left.Id] }] }, token);
        Assert.Equal(DeliveryState.Confirmed, sent.State);
        Assert.Single((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
    }

    /// <summary>合成来源由夹具驱动识别，与 HTTP 来源分离。</summary>
    private static MediaSource Source() => new() { Kind = "fixture", AdapterId = "fixture-adapter", BotAccountId = "bot", Data = JsonSerializer.SerializeToElement(new { file = "test" }) };
}
