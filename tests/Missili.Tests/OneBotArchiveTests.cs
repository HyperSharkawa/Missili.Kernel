using System.Text;
using System.Text.Json;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>实际协议查询和归档工作，包括标准与显式 SnowLuma 样本的独立证据。</summary>
public sealed class OneBotArchiveTests
{
    /// <summary>适配器重载取消正在查询的语音，原引用恢复归档且不重新分发消息。</summary>
    [Fact]
    public async Task ReloadResumesAdapterDependentMediaInsteadOfMarkingPermanentFailure()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, settings => settings["compatibilityProfile"] = "SnowLuma");
        int requests = 0;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_record") return false;
            if (Interlocked.Increment(ref requests) == 1) { first.TrySetResult(); return true; }
            await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { base64 = "AQID" }), cancellation); return true;
        };
        await fixture.Peer.SendAsync(fixture.Role, WithSegments(fixture, 99, """[{"type":"record","data":{"file":"held.silk"}}]"""), token: token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        MessageSnapshot message = await fixture.WaitMessageAsync("99", token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync("missili.onebot11", OfficialPluginPackages.GetPath("onebot"), fixture.Configuration(), token)).State);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using MediaReadResult media = await fixture.Runtime.Media.OpenReadAsync(message.Segments[0].MediaIds[0], deadline.Token);
        Assert.Equal(MediaState.Available, media.Status.State);
        Assert.Equal(2, requests);
        Assert.Single((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
    }

    /// <summary>合成媒体 HTTP 服务下载入库，出站只发送 Base64 和标准允许字段。</summary>
    [Fact]
    public async Task HttpMediaIsArchivedAndSentWithoutInternalFields()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        string segment = JsonSerializer.Serialize(new[] { new Dictionary<string, object>
        { ["type"] = "image", ["data"] = new Dictionary<string, object> { ["file"] = "image.bin", ["url"] = fixture.Peer.BaseUrl + "/media", ["extra"] = "keep" } } });
        await fixture.Peer.SendAsync(fixture.Role, WithSegments(fixture, 100, segment), token: token);
        MessageSnapshot stored = await fixture.WaitMessageAsync("100", token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using MediaReadResult media = await fixture.Runtime.Media.OpenReadAsync(stored.Segments[0].MediaIds[0], deadline.Token);
        Assert.Equal(MediaState.Available, media.Status.State);
        SendResult sent = Assert.Single((await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(OneBotFixture.Address(), stored.Segments), token)).Messages);
        Assert.Equal(DeliveryState.Confirmed, sent.State);
        JsonElement data = (await fixture.WaitActionAsync("send_group_msg", token)).Parameters.GetProperty("message")[0].GetProperty("data");
        Assert.Equal("base64://" + Convert.ToBase64String(fixture.Peer.Media), data.GetProperty("file").GetString());
        Assert.False(data.TryGetProperty("url", out _)); Assert.False(data.TryGetProperty("extra", out _));
        Assert.Equal("keep", stored.Segments[0].Payload!.Value.GetProperty("extra").GetString());
    }

    /// <summary>平台返回远端路径但未映射时明确失败，普通文本仍然保存。</summary>
    [Fact]
    public async Task RemoteFilePathIsNotTreatedAsLocalWithoutMapping()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_image") return false;
            await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { file = "/remote/image.png" }), cancellation); return true;
        };
        await fixture.Peer.SendAsync(fixture.Role, WithSegments(fixture, 101, """[{"type":"image","data":{"file":"remote-image"}},{"type":"text","data":{"text":"正常文字"}}]"""), token: token);
        MessageSnapshot stored = await fixture.WaitMessageAsync("101", token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using MediaReadResult media = await fixture.Runtime.Media.OpenReadAsync(stored.Segments[0].MediaIds[0], deadline.Token);
        Assert.Equal(MediaState.Failed, media.Status.State); Assert.Null(media.Content);
        Assert.Equal("正常文字", stored.Segments[1].Text);
    }

    /// <summary>标准转发保存成功分支、嵌套节点和循环说明，查询失败不会伪造空正文。</summary>
    [Fact]
    public async Task StandardForwardPreservesPartialNodesAndCycleDiagnostics()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token);
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_forward_msg") return false;
            string id = action.Parameters.GetProperty("id").GetString()!;
            string json = id switch
            {
                "root" => """{"message":[{"type":"node","data":{"user_id":"30001","nickname":"父","content":[{"type":"text","data":{"text":"父层"}},{"type":"forward","data":{"id":"child"}},{"type":"forward","data":{"id":"missing"}}]}}]}""",
                "child" => """{"message":[{"type":"node","data":{"user_id":"30002","nickname":"子","content":[{"type":"text","data":{"text":"子层"}},{"type":"forward","data":{"id":"root"}}]}}]}""",
                _ => "null"
            };
            using JsonDocument response = JsonDocument.Parse(json);
            await action.RespondAsync(id == "missing" ? "failed" : "ok", id == "missing" ? 100 : 0, response.RootElement.Clone(), cancellation); return true;
        };
        Guid id = await fixture.Runtime.Contents.RequestContentAsync(new ContentSource { Kind = "forward", Address = OneBotFixture.Address(), PlatformId = "root" }, token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        StructuredContentSnapshot content = await fixture.Runtime.Contents.WaitContentAsync(id, deadline.Token);
        Assert.Equal(ContentState.Partial, content.State); Assert.Equal(2, content.Nodes.Length);
        Assert.Contains(content.Issues, issue => issue.Code == "cycle"); Assert.Contains(content.Issues, issue => issue.Code == "fetch-failed");
        Assert.Equal("父层", content.Nodes[0].Segments[0].Text); Assert.Equal("子层", content.Nodes[1].Segments[0].Text);
        Assert.Empty((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
    }

    /// <summary>真实空转发与限制截断是不同状态，深度限制不继续查询子引用。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyForwardAndDepthLimitAreDistinguishable(bool empty)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, settings => settings["maxForwardDepth"] = 1);
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_forward_msg") return false;
            using JsonDocument response = JsonDocument.Parse(empty ? "{\"message\":[]}" : """{"message":[{"type":"node","data":{"user_id":"1","nickname":"n","content":[{"type":"forward","data":{"id":"child"}}]}}]}""");
            await action.RespondAsync("ok", 0, response.RootElement.Clone(), cancellation); return true;
        };
        Guid id = await fixture.Runtime.Contents.RequestContentAsync(new ContentSource { Kind = "forward", Address = OneBotFixture.Address(), PlatformId = "root" }, token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        StructuredContentSnapshot content = await fixture.Runtime.Contents.WaitContentAsync(id, deadline.Token);
        Assert.Equal(empty ? ContentState.Complete : ContentState.Limited, content.State);
        if (empty) Assert.Empty(content.Nodes); else Assert.Contains(content.Issues, issue => issue.Code == "depth-limit");
        Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == "get_forward_msg");
    }

    /// <summary>SnowLuma 的已脱敏转发形态只在显式兼容模式启用，标准模式明确失败。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnowLumaForwardFixtureRequiresExplicitCompatibility(bool compatible)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token,
            settings => settings["compatibilityProfile"] = compatible ? "SnowLuma" : "Standard");
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_forward_msg") return false;
            using JsonDocument fixtureData = JsonDocument.Parse("""{"messages":[{"sender":{"nickname":"测试用户"},"message":[{"type":"text","data":{"text":"脱敏样本正文"}}]}]}""");
            await action.RespondAsync("ok", 0, fixtureData.RootElement.Clone(), cancellation); return true;
        };
        Guid id = await fixture.Runtime.Contents.RequestContentAsync(new ContentSource { Kind = "forward", Address = OneBotFixture.Address(), PlatformId = "sample" }, token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        StructuredContentSnapshot content = await fixture.Runtime.Contents.WaitContentAsync(id, deadline.Token);
        Assert.Equal(compatible ? ContentState.Complete : ContentState.Failed, content.State);
        if (compatible) { Assert.Null(content.Nodes[0].SenderId); Assert.Equal("测试用户", content.Nodes[0].SenderName); }
    }

    /// <summary>Base64 语音扩展显式记录转换格式，标准模式不会将扩展当作标准。</summary>
    [Fact]
    public async Task SnowLumaRecordBase64IsMarkedAsConvertedRepresentation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, settings => settings["compatibilityProfile"] = "SnowLuma");
        fixture.Peer.ResponseHandler = async (action, cancellation) =>
        {
            if (action.Action != "get_record") return false;
            Assert.Equal("wav", action.Parameters.GetProperty("out_format").GetString());
            await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new { base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-wav")) }), cancellation); return true;
        };
        await fixture.Peer.SendAsync(fixture.Role, WithSegments(fixture, 103, """[{"type":"record","data":{"file":"voice.silk"}}]"""), token: token);
        MessageSnapshot stored = await fixture.WaitMessageAsync("103", token);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using MediaReadResult media = await fixture.Runtime.Media.OpenReadAsync(stored.Segments[0].MediaIds[0], deadline.Token);
        Assert.Equal(MediaState.Available, media.Status.State);
        Assert.False(media.Status.Representation!.Value.GetProperty("original").GetBoolean());
        Assert.Equal("wav", media.Status.Representation!.Value.GetProperty("format").GetString());
    }

    /// <summary>替换合成事件的消息内容，不使用生产编码器制造预期值。</summary>
    internal static string WithSegments(OneBotFixture fixture, long id, string segments)
    {
        using JsonDocument input = JsonDocument.Parse(fixture.Peer.Message(id, ""));
        using JsonDocument content = JsonDocument.Parse(segments);
        Dictionary<string, JsonElement> result = input.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        result["message"] = content.RootElement.Clone(); return JsonSerializer.Serialize(result);
    }
}
