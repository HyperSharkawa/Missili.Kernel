using System.Runtime.Loader;
using System.Text.Json;
using Missili.Abstractions;
using Missili.OneBot.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实协议连接验证官方适配器，不静态引用待卸载实现 DLL。</summary>
public sealed class OneBotProtocolTests
{
    /// <summary>四种组合都必须完成协议收发，实际插件产生回复且历史记录收到确认。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FourConnectionLayoutsReceiveReplyAndQuery(bool reverse, bool split)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(reverse, split, token);
        await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "keyword"),
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["keyword"] = "hello", ["reply"] = "synthetic-reply" }), token);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(50, "hello"), fragmentBytes: 7, token);
        MessageSnapshot received = await fixture.WaitMessageAsync("50", token);
        Assert.Equal("hello", Assert.Single(received.Segments).Text);
        MockAction reply = await fixture.WaitActionAsync("send_group_msg", token);
        Assert.Equal("synthetic-reply", reply.Parameters.GetProperty("message")[0].GetProperty("data").GetProperty("text").GetString());
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        HistoryPage page;
        do { page = await fixture.Runtime.History.QueryAsync(TestStore.Query(), deadline.Token); if (page.Messages.Length < 2) await Task.Delay(1, deadline.Token); }
        while (!page.Messages.Any(item => item.Direction == MessageDirection.Outgoing && item.Delivery == DeliveryState.Confirmed));
        Assert.Equal(2, page.Messages.Length);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "Missili.Adapters.OneBot");
    }

    /// <summary>数组不解码 CQ 转义，CQ 字符串仅解码一层，异常段与未知扩展仍保存。</summary>
    [Fact]
    public async Task CqEscapingUnknownSegmentsAndLargeIdentityArePreserved()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        using JsonDocument message = JsonDocument.Parse(fixture.Peer.Message(60, "unused"));
        Dictionary<string, JsonElement> fields = message.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        fields["user_id"] = JsonSerializer.SerializeToElement(9007199254740993L);
        fields["message"] = JsonSerializer.SerializeToElement("&#91;literal&#93;&amp;#91;&#44;[CQ:share,title=a&#44;b=x,url=https://example.invalid/?a=1&amp;b=2][CQ:future,x=1]");
        await fixture.Peer.SendAsync(fixture.Role, JsonSerializer.Serialize(fields), fragmentBytes: 1, token);
        MessageSnapshot cq = await fixture.WaitMessageAsync("60", token);
        Assert.Equal("9007199254740993", cq.SenderId);
        Assert.Equal("[literal]&#91;&#44;", cq.Segments[0].Text);
        Assert.Equal("a,b=x", cq.Segments[1].Payload!.Value.GetProperty("title").GetString());
        Assert.Equal("onebot11.future", cq.Segments[2].Kind);
        fields["message_id"] = JsonSerializer.SerializeToElement(61);
        using JsonDocument segments = JsonDocument.Parse("""[{"type":"text","data":{"text":"&#91;原文&#93;","extra":1}},{"type":"image","data":{"file":42}},{"type":"future","data":{"nested":{"v":1}}}]""");
        fields["message"] = segments.RootElement.Clone();
        await fixture.Peer.SendAsync(fixture.Role, JsonSerializer.Serialize(fields), token: token);
        MessageSnapshot array = await fixture.WaitMessageAsync("61", token);
        Assert.Equal("&#91;原文&#93;", array.Segments[0].Text);
        Assert.Equal(1, array.Segments[0].Payload!.Value.GetProperty("extra").GetInt32());
        Assert.Equal(SegmentParseState.Invalid, array.Segments[1].ParseState);
        Assert.Equal(SegmentParseState.Unrecognized, array.Segments[2].ParseState);
        fields["message_id"] = JsonSerializer.SerializeToElement(62);
        fields["message_type"] = JsonSerializer.SerializeToElement("private");
        fields["sub_type"] = JsonSerializer.SerializeToElement("group");
        await fixture.Peer.SendAsync(fixture.Role, JsonSerializer.Serialize(fields), token: token);
        MessageSnapshot temporary = await fixture.WaitMessageAsync("62", token);
        Assert.Equal("private", temporary.Address.ConversationKind);
        Assert.Equal(20001, temporary.Metadata!.Value.GetProperty("group_id").GetInt64());
    }

    /// <summary>无效帧后正常消息仍能处理，元事件不进入长期业务历史。</summary>
    [Fact]
    public async Task BadJsonAndMetaEventsDoNotCreateFakeHistory()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(true, false, token);
        await fixture.Peer.SendAsync(fixture.Role, "{invalid", token: token);
        await fixture.Peer.SendAsync(fixture.Role, """{"self_id":10001,"post_type":"meta_event","meta_event_type":"heartbeat","status":{"online":true},"interval":1000} """, token: token);
        await fixture.Peer.SendAsync(fixture.Role, fixture.Peer.Message(70, "中文🙂"), fragmentBytes: 1, token);
        Assert.Equal("中文🙂", (await fixture.WaitMessageAsync("70", token)).Segments[0].Text);
        Assert.Empty((await fixture.Runtime.Events.QueryEventsAsync(new EventQuery(), token)).Events);
        Assert.Single((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
    }
}
