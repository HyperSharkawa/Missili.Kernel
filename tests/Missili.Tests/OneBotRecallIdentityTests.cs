using System.Text.Json;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>按标准的准确消息身份处理私聊撤回，不用时间或正文猜测错误平台标识。</summary>
public sealed class OneBotRecallIdentityTests
{
    /// <summary>正确撤回标识更新原文可见性；不匹配标识只保留事件，不能误标已有消息。
    /// 依据：https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/event/notice.md 。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivateRecallRequiresExactPlatformIdentity(bool matching)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(901, "original-private", group: false), token: token);
        MessageSnapshot original = await fixture.WaitMessageAsync("901", token);
        long target = matching ? 901 : 902;
        string notice = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["time"] = 1700000001L,
            ["self_id"] = 10001L,
            ["post_type"] = "notice",
            ["notice_type"] = "friend_recall",
            ["user_id"] = 30001L,
            ["message_id"] = target
        });
        await fixture.Peer.SendAsync(fixture.Role, notice, token: token);
        // 同一有序连接的后续消息入库后，先前撤回事务也应已经提交。
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(903, "barrier", group: false), token: token);
        await fixture.WaitMessageAsync("903", token);
        MessageSnapshot current = (await fixture.Runtime.History.GetAsync(original.Id, token))!;
        Assert.Equal(matching, current.Recalled);
        Assert.Equal("original-private", Assert.Single(current.Segments).Text);
        PlatformEventSnapshot stored = Assert.Single((await fixture.Runtime.Events.QueryEventsAsync(new EventQuery { Type = "friend_recall" }, token)).Events);
        Assert.Equal(target.ToString(System.Globalization.CultureInfo.InvariantCulture), stored.Event.TargetMessageId);
        Assert.Equal("private", stored.Event.ConversationKind);
    }
}
