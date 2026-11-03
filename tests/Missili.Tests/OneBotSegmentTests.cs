using System.Text.Json;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>标准消息段收发方向矩阵，预期报文直接按规范编写。</summary>
public sealed class OneBotSegmentTests
{
    /// <summary>所有标准段都必须明确保存为已解释内容或带诊断的错误方向。</summary>
    [Theory]
    [InlineData("text", "{\"text\":\"actual\"}", "text", true)]
    [InlineData("face", "{\"id\":\"123\"}", "qq.face", true)]
    [InlineData("at", "{\"qq\":\"all\"}", "mention", true)]
    [InlineData("reply", "{\"id\":\"123\"}", "reply", true)]
    [InlineData("rps", "null", "qq.rps", true)]
    [InlineData("dice", "{}", "qq.dice", true)]
    [InlineData("poke", "{\"type\":\"126\",\"id\":\"2003\",\"name\":\"sample\"}", "qq.poke", true)]
    [InlineData("share", "{\"url\":\"https://example.invalid/\",\"title\":\"sample\"}", "share", true)]
    [InlineData("contact", "{\"type\":\"qq\",\"id\":\"123\"}", "qq.contact", true)]
    [InlineData("contact", "{\"type\":\"group\",\"id\":\"123\"}", "qq.contact", true)]
    [InlineData("location", "{\"lat\":\"39.9\",\"lon\":\"116.3\"}", "location", true)]
    [InlineData("json", "{\"data\":\"{\\\"app\\\":1}\"}", "onebot11.json", true)]
    [InlineData("xml", "{\"data\":\"<xml/>\"}", "onebot11.xml", true)]
    [InlineData("shake", "{}", "qq.shake", false)]
    [InlineData("anonymous", "{}", "qq.anonymous", false)]
    [InlineData("music", "{\"type\":\"qq\",\"id\":\"1\"}", "qq.music", false)]
    [InlineData("node", "{\"id\":\"1\"}", "onebot11.node", false)]
    public async Task StandardReceiveDirectionsAreExplicit(string wireType, string data, string kind, bool valid)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        using JsonDocument payload = JsonDocument.Parse(data);
        string segments = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["type"] = wireType, ["data"] = payload.RootElement } });
        await fixture.Peer.SendAsync(fixture.Role, OneBotArchiveTests.WithSegments(fixture, 300, segments), token: token);
        MessageSegment segment = Assert.Single((await fixture.WaitMessageAsync("300", token)).Segments);
        Assert.Equal(kind, segment.Kind);
        Assert.Equal(valid ? SegmentParseState.Parsed : SegmentParseState.Invalid, segment.ParseState);
    }

    /// <summary>引用只输出标准 id，转发发送使用 node 而不发送接收资源 id。</summary>
    [Fact]
    public async Task ReplyAndForwardSendOnlyStandardFields()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        MessageSegment reply = SegmentValues.Reply("7") with
        {
            Payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["messageId"] = "7", ["authorSnapshot"] = "private", ["archiveState"] = "complete" })
        };
        MessageSegment forward = SegmentValues.Forward([new ForwardNode { UserId = "30001", Nickname = "合成作者", Segments = [SegmentValues.Text("节点正文")] }]);
        SendResult result = await fixture.Runtime.Sender.SendAsync(new OutgoingMessage { Address = OneBotFixture.Address(), Segments = [reply, forward] }, token);
        Assert.Equal(DeliveryState.Confirmed, result.State);
        JsonElement message = (await fixture.WaitActionAsync("send_group_msg", token)).Parameters.GetProperty("message");
        Assert.Single(message[0].GetProperty("data").EnumerateObject());
        Assert.Equal("7", message[0].GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("node", message[1].GetProperty("type").GetString());
        Assert.Equal("节点正文", message[1].GetProperty("data").GetProperty("content")[0].GetProperty("data").GetProperty("text").GetString());
        SendResult unknown = await fixture.Runtime.Sender.SendAsync(new OutgoingMessage { Address = OneBotFixture.Address(), Segments = [new MessageSegment { Kind = "onebot11.future" }] }, token);
        Assert.Equal(DeliveryState.Failed, unknown.State);
        Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == "send_group_msg");
    }
}
