using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>直接加载交付给插件作者的示例构建产物，避免示例只有编译证据。</summary>
public sealed class SamplePluginTests
{
    /// <summary>适配器观察到的 Bot 发送事件保留方向，不作为新用户输入触发关键词回复。</summary>
    [Fact]
    public async Task ObservedOutgoingMessageDoesNotTriggerIncomingListener()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        JsonElement configuration = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["keyword"] = "ping", ["reply"] = "pong" });
        await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "keyword"), configuration, token);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message("ping", "observed") with
        { Direction = MessageDirection.Outgoing, SenderId = TestStore.Address().BotAccountId }, token);
        MessageSnapshot stored = await fixture.FinishedAsync(receipt.Message!.Id, token);
        Assert.Equal(MessageDirection.Outgoing, stored.Direction);
        Assert.Equal(DeliveryState.Confirmed, stored.Delivery);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>实际关键词示例不等待上一条消息的媒体原件。</summary>
    [Fact]
    public async Task KeywordSampleRepliesWhileMediaIsPending()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(new byte[] { 1, 2 }, context.RequestAborted);
        }, token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("media", "v1", token), RuntimeFixture.Configuration("media"), token);
        JsonElement configuration = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["keyword"] = "ping", ["reply"] = "pong" });
        PluginStatus sample = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "keyword"), configuration, token);
        Assert.Equal(PluginState.Active, sample.State);
        ReceiveReceipt image = await fixture.Runtime.ReceiveAsync(TestStore.MediaMessage(server.BaseAddress), token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        ReceiveReceipt text = await fixture.Runtime.ReceiveAsync(TestStore.Message("ping"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "pong", token);
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
        await fixture.FinishedAsync(image.Message!.Id, token);
        await fixture.FinishedAsync(text.Message!.Id, token);
        Assert.Single(fixture.Transport.Requests, message => message.Segments[0].Text == "pong");
    }

    /// <summary>实际历史示例通过宿主查询接口读取已经保存的当前会话。</summary>
    [Fact]
    public async Task HistorySampleQueriesActualDatabase()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "history"), JsonSerializer.SerializeToElement(new Dictionary<string, string>()), token);
        ReceiveReceipt first = await fixture.Runtime.ReceiveAsync(TestStore.Message("first"), token);
        await fixture.FinishedAsync(first.Message!.Id, token);
        ReceiveReceipt command = await fixture.Runtime.ReceiveAsync(TestStore.Message("/history"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "读取到 2 条会话历史。", token);
        await fixture.FinishedAsync(command.Message!.Id, token);
        Assert.Single(fixture.Transport.Requests);
    }
}
