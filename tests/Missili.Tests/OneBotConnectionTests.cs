using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.OneBot.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实连接上的鉴权、关联、迟到回执和离线语义。</summary>
public sealed class OneBotConnectionTests
{
    /// <summary>反向入口拒绝缺失或错误 Token、错误账号和布局角色。</summary>
    [Theory]
    [InlineData(null, 10001, "Universal")]
    [InlineData("wrong", 10001, "Universal")]
    [InlineData("synthetic-token", 10002, "Universal")]
    [InlineData("synthetic-token", 10001, "API")]
    public async Task ReverseRejectsInvalidCredentialsIdentityOrRole(string? tokenValue, long account, string role)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token, connect: false);
        await using MockPeer peer = new() { Token = tokenValue, SelfId = account };
        await Assert.ThrowsAsync<WebSocketException>(() => peer.ConnectAsync(fixture.HostUrl.Replace("http:", "ws:") + "/adapters/qq-test/ws", role, token));
        Assert.False(Assert.Single(fixture.Runtime.Adapters.GetStatuses()).Connected);
    }

    /// <summary>显式无鉴权模式两种方向均能连接，缺省仍为 Token。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNoAuthenticationWorks(bool reverse)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(reverse, false, token,
            settings => { settings.Remove("token"); settings["authentication"] = "None"; }, peer => peer.Token = null);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(1, "无鉴权合成测试"), token: token);
        Assert.Equal("无鉴权合成测试", (await fixture.WaitMessageAsync("1", token)).Segments[0].Text);
    }

    /// <summary>不同 echo 的回执乱序到达仍只完成各自操作。</summary>
    [Fact]
    public async Task ApiResponsesAreCorrelatedOutOfOrder()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        fixture.Peer.AutoRespond = false;
        Task<PlatformOperationResult> first = fixture.Runtime.Adapters.InvokeAsync("qq-test", "10001", "get_status", Empty(), token);
        Task<PlatformOperationResult> second = fixture.Runtime.Adapters.InvokeAsync("qq-test", "10001", "get_version_info", Empty(), token);
        MockAction a = await fixture.WaitActionAsync("get_status", token), b = await fixture.WaitActionAsync("get_version_info", token);
        await b.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { marker = "second" }), token);
        Assert.Equal("second", (await second).Data!.Value.GetProperty("marker").GetString());
        Assert.False(first.IsCompleted);
        await a.RespondAsync("failed", 1404, null, token);
        Assert.Equal(1404, (await first).Retcode);
        Assert.Equal(PlatformOperationState.Failed, (await first).State);
    }

    /// <summary>发送超时只变为未知，迟到确认修正状态且没有第二次传输。</summary>
    [Fact]
    public async Task LateReceiptCorrectsUnknownWithoutResending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, clock: clock);
        fixture.Peer.AutoRespond = false;
        Task<BatchSendResult> send = fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("late")]), token);
        MockAction action = await fixture.WaitActionAsync("send_group_msg", token);
        clock.Advance(TimeSpan.FromSeconds(31));
        SendResult timeout = Assert.Single((await send.WaitAsync(TimeSpan.FromSeconds(5), token)).Messages);
        Assert.Equal(DeliveryState.Unknown, timeout.State);
        await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { message_id = 1234 }), token);
        MessageSnapshot stored = await fixture.WaitMessageAsync("1234", token);
        Assert.Equal(timeout.MessageId, stored.Id);
        Assert.Equal(DeliveryState.Confirmed, stored.Delivery);
        Assert.Single(fixture.Peer.RecordedActions, item => item.GetProperty("action").GetString() == "send_group_msg");
    }

    /// <summary>异步受理和缺少有效消息标识的成功响应都不能记成确认成功。</summary>
    [Theory]
    [InlineData("async", 1, DeliveryState.Accepted)]
    [InlineData("ok", 0, DeliveryState.Unknown)]
    [InlineData("failed", 100, DeliveryState.Failed)]
    public async Task UnconfirmedResponsesKeepAccurateState(string status, int retcode, DeliveryState expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        fixture.Peer.AutoRespond = false;
        Task<BatchSendResult> sending = fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("state")]), token);
        MockAction action = await fixture.WaitActionAsync("send_group_msg", token);
        await action.RespondAsync(status, retcode, null, token);
        Assert.Equal(expected, Assert.Single((await sending).Messages).State);
        Assert.Equal(expected, (await fixture.Runtime.History.GetAsync(Assert.Single((await sending).Messages).MessageId!.Value, token))!.Delivery);
    }

    /// <summary>请求进入网络后的取消与离线新发送有不同结果，并且不跨重连排队。</summary>
    [Fact]
    public async Task CancellationAfterTransmissionIsUnknownAndOfflineSendFails()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token);
        fixture.Peer.AutoRespond = false;
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<BatchSendResult> sending = fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("cancel")]), cancel.Token);
        await fixture.WaitActionAsync("send_group_msg", token); cancel.Cancel();
        Assert.Equal(DeliveryState.Unknown, Assert.Single((await sending).Messages).State);
        fixture.Peer.Abort("Universal");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (fixture.Runtime.Adapters.GetStatuses().Any(item => item.ApiAvailable)) await Task.Delay(1, deadline.Token);
        SendResult offline = Assert.Single((await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("offline")]), token)).Messages);
        Assert.Equal(DeliveryState.Failed, offline.State);
        Assert.Single(fixture.Peer.RecordedActions, item => item.GetProperty("action").GetString() == "send_group_msg");
    }

    /// <summary>构造普通空参数，不调用生产协议编码器。</summary>
    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new Dictionary<string, string>());
}
