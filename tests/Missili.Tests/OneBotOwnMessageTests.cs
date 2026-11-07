using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.OneBot.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>自身发送上报属于实现兼容，按精确平台身份与本地意图合并。</summary>
public sealed class OneBotOwnMessageTests
{
    /// <summary>上报先到、响应先到和响应超时后迟到三种顺序都只有一个规范历史。</summary>
    [Theory]
    [InlineData("observation-first")]
    [InlineData("response-first")]
    [InlineData("late-response")]
    public async Task ObservedOwnMessageAndReceiptShareCanonicalHistory(string ordering)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, settings => settings["compatibilityProfile"] = "SnowLuma", clock: clock);
        fixture.Peer.AutoRespond = false;
        Task<BatchSendResult> sending = fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), [SegmentValues.Text("own")]), token);
        MockAction action = await fixture.WaitActionAsync("send_group_msg", token);
        if (ordering == "late-response") { clock.Advance(TimeSpan.FromSeconds(31)); Assert.Equal(DeliveryState.Unknown, Assert.Single((await sending).Messages).State); }
        if (ordering == "response-first") { await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { message_id = 401 }), token); await sending; }
        using JsonDocument frame = JsonDocument.Parse(fixture.Peer.Message(401, "own"));
        Dictionary<string, JsonElement> observed = frame.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        observed["post_type"] = JsonSerializer.SerializeToElement("message_sent"); observed["user_id"] = JsonSerializer.SerializeToElement(10001);
        await fixture.Peer.SendAsync(fixture.Role, JsonSerializer.Serialize(observed), token: token);
        MessageSnapshot observation = await fixture.WaitMessageAsync("401", token);
        if (ordering != "response-first") await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { message_id = 401 }), token);
        SendResult sent = Assert.Single((await sending).Messages);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await fixture.Runtime.History.GetAsync(sent.MessageId!.Value, deadline.Token))!.Delivery != DeliveryState.Confirmed) await Task.Delay(1, deadline.Token);
        HistoryPage history = await fixture.Runtime.History.QueryAsync(TestStore.Query(), token);
        Assert.Single(history.Messages);
        Assert.Equal(MessageDirection.Outgoing, history.Messages[0].Direction);
        Assert.Equal(history.Messages[0].Id, (await fixture.Runtime.History.GetAsync(observation.Id, token))!.Id);
        Assert.Single(fixture.Peer.RecordedActions, request => request.GetProperty("action").GetString() == "send_group_msg");
    }
}
