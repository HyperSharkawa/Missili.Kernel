using System.Runtime.CompilerServices;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Nexus;
using Missili.OneBot.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实官方驱动在活动连接、业务通知和包重载下的生命周期。</summary>
public sealed class OneBotLifecycleTests
{
    /// <summary>四种布局的连接和回调都不能阻止卸载旧程序集。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveOneBotPluginCanBeUnloaded(bool reverse, bool split)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(reverse, split, token);
        WeakReference reference = await StopAsync(fixture, token);
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
        Assert.Empty(fixture.Runtime.Adapters.GetStatuses());
        SendResult result = Assert.Single((await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("stopped")]), token)).Messages);
        Assert.Equal(DeliveryState.Failed, result.State);
    }

    /// <summary>移出加载代次返回值所在栈帧，只保留卸载弱引用。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StopAsync(OneBotFixture fixture, CancellationToken token)
    {
        Assert.Equal(PluginState.Stopped, (await fixture.Runtime.Plugins.StopAsync("missili.onebot11", token)).State);
        return ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference("missili.onebot11")!;
    }

    /// <summary>反向同角色新连接替换旧连接后，旧关闭回调不清掉新实例。</summary>
    [Fact]
    public async Task ReplacementConnectionSurvivesOldConnectionClose()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token);
        await using MockPeer replacement = new() { Token = "synthetic-token" };
        await replacement.ConnectAsync(fixture.HostUrl.Replace("http:", "ws:") + "/adapters/qq-test/ws", "Universal", token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await replacement.NextActionAsync(deadline.Token)).Action != "get_login_info") { }
        await replacement.SendAsync("Universal", replacement.Message(200, "new connection"), token: deadline.Token);
        Assert.Equal("new connection", (await fixture.WaitMessageAsync("200", token)).Segments[0].Text);
        while (!fixture.Runtime.Adapters.GetStatuses().Any(item => item.ApiAvailable)) await Task.Delay(1, deadline.Token);
        Assert.Equal(PlatformOperationState.Succeeded, (await fixture.Runtime.Adapters.GetStatusAsync("qq-test", "10001", token)).State);
    }

    /// <summary>标准通知撤回与请求持久化，心跳和连接事件没有长期记录。</summary>
    [Fact]
    public async Task NoticeBeforeMessageAndRequestAreStoredWithoutAutomaticApproval()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        await fixture.Peer.SendAsync(fixture.Role, """{"time":1700000000,"self_id":10001,"post_type":"notice","notice_type":"group_recall","group_id":20001,"user_id":30001,"operator_id":30002,"message_id":201} """, token: token);
        await fixture.Peer.SendAsync(fixture.Role, """{"time":1700000000,"self_id":10001,"post_type":"request","request_type":"group","sub_type":"add","group_id":20001,"user_id":30001,"flag":"synthetic-request","comment":"合成申请"} """, token: token);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(201, "保留撤回正文"), token: token);
        MessageSnapshot stored = await fixture.WaitMessageAsync("201", token);
        Assert.True(stored.Recalled);
        Assert.Equal("保留撤回正文", stored.Segments[0].Text);
        EventPage events = await fixture.Runtime.Events.QueryEventsAsync(new EventQuery(), token);
        Assert.Equal(2, events.Events.Length);
        Assert.DoesNotContain(fixture.Peer.RecordedActions, item => item.GetProperty("action").GetString() == "set_group_add_request");
        PlatformOperationResult approved = await fixture.Runtime.Adapters.SetGroupRequestAsync("qq-test", "10001", "synthetic-request", "add", true, token: token);
        Assert.Equal(PlatformOperationState.Succeeded, approved.State);
    }

    /// <summary>大帧暂存后仍保留完整段值，超限帧断开连接且不会入库。</summary>
    [Fact]
    public async Task PayloadSpillAndMaximumAreEnforced()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token, settings =>
        { settings["memoryThreshold"] = 256; settings["maxMessageBytes"] = 16384; settings["inboundByteBudget"] = 32768; });
        string text = new('中', 1000);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(202, text), fragmentBytes: 111, token);
        Assert.Equal(text, (await fixture.WaitMessageAsync("202", token)).Segments[0].Text);
        try { await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(203, new string('x', 10000)), token: token); }
        catch (System.Net.WebSockets.WebSocketException) { }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (fixture.Runtime.Adapters.GetStatuses().Any(item => item.Connected)) await Task.Delay(1, deadline.Token);
        Assert.Single((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
    }
}
