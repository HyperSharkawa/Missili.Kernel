using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实组装运行时验证接收、插件、模拟发送及数据库的完整链路。</summary>
public sealed class RuntimeTests
{
    /// <summary>只有一个消息槽时，完成通知仍会持续推进已落库积压，不依赖后来新消息唤醒。</summary>
    [Fact]
    public async Task SingleFlowCapacityDrainsPersistedBacklog()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, options =>
        {
            options.MessageConcurrency = 1;
            options.PluginConcurrency = 1;
            options.PerPluginConcurrency = 1;
            options.IngressCapacity = 4;
        });
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("sample", "v1", token), RuntimeFixture.Configuration(), token);
        Task<ReceiveReceipt>[] inputs = Enumerable.Range(0, 24)
            .Select(index => fixture.Runtime.ReceiveAsync(TestStore.Message(index.ToString()), token)).ToArray();
        ReceiveReceipt[] receipts = await Task.WhenAll(inputs);
        await Task.WhenAll(receipts.Select(receipt => fixture.FinishedAsync(receipt.Message!.Id, token)));
        Assert.Equal(24, fixture.Transport.Requests.Count);
    }

    /// <summary>收到消息写库失败时，不向已加载插件分发未保存的内容。</summary>
    [Fact]
    public async Task FailedIncomingWriteDoesNotDispatchPlugin()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("sample", "v1", token), RuntimeFixture.Configuration(), token);
        await ExecuteSqlAsync(fixture, "CREATE TRIGGER fail_incoming BEFORE INSERT ON Messages WHEN NEW.Direction=0 BEGIN SELECT RAISE(ABORT, 'synthetic'); END;", token);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message(), token);
        Assert.Null(receipt.Message);
        Assert.NotNull(receipt.Error);
        Assert.Empty((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>处理器修改只影响后续处理，原始用户历史与发送回执分别保留。</summary>
    [Fact]
    public async Task FullPipelinePreservesOriginalAndStoresReply()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("sample", "v1", token);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token)).State);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message("hello", "unique"), token);
        OutgoingMessage reply = await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "v1:v1>hello", token);
        Assert.Equal(TestStore.Address(), reply.Address);
        MessageSnapshot original = await fixture.FinishedAsync(receipt.Message!.Id, token);
        Assert.Equal("hello", original.Segments[0].Text);
        ReceiveReceipt duplicate = await fixture.Runtime.ReceiveAsync(TestStore.Message("hello", "unique"), token);
        Assert.True(duplicate.Duplicate);
        HistoryPage history = await fixture.Runtime.History.QueryAsync(TestStore.Query(), token);
        Assert.Equal(2, history.Messages.Length);
        Assert.Equal(DeliveryState.Confirmed, history.Messages.Single(message => message.Direction == MessageDirection.Outgoing).Delivery);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>显式拦截与异常放行必须区分，二者都不删除历史。</summary>
    [Theory]
    [InlineData("intercept", 0)]
    [InlineData("throw-interceptor", 1)]
    [InlineData("command-fail", 0)]
    [InlineData("command-match-fail", 1)]
    public async Task RoutingFailuresRespectEstablishedPolicy(string mode, int expectedReplies)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("sample", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(mode), token);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message(mode.StartsWith("command", StringComparison.Ordinal) ? "/history" : "hello"), token);
        await fixture.FinishedAsync(receipt.Message!.Id, token);
        Assert.Equal(expectedReplies, fixture.Transport.Requests.Count);
        Assert.NotNull(await fixture.Runtime.History.GetAsync(receipt.Message.Id, token));
        Assert.Equal(PluginState.Active, Assert.Single(fixture.Runtime.Plugins.GetStatuses()).State);
    }

    /// <summary>一个监听器异常不会影响另一个插件的正常回复。</summary>
    [Fact]
    public async Task ListenerFailureDoesNotStopOtherPlugins()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("a", "v1", token), RuntimeFixture.Configuration("throw-listener"), token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("b", "v2", token), RuntimeFixture.Configuration(), token);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message("hello"), token);
        await fixture.FinishedAsync(receipt.Message!.Id, token);
        Assert.Equal("v2:v2>hello", Assert.Single(fixture.Transport.Requests).Segments[0].Text);
    }

    /// <summary>媒体归档未放行时，同一插件的下一条纯文本仍能回复。</summary>
    [Fact]
    public async Task WaitingMediaDoesNotBlockLaterText()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }, context.RequestAborted);
        }, token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("sample", "v1", token), RuntimeFixture.Configuration("media"), token);
        ReceiveReceipt image = await fixture.Runtime.ReceiveAsync(TestStore.MediaMessage(server.BaseAddress), token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        ReceiveReceipt text = await fixture.Runtime.ReceiveAsync(TestStore.Message("keyword"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "v1:keyword", token);
        Assert.NotEqual(MediaState.Available, (await fixture.Runtime.Media.GetStatusAsync(image.Message!.Segments[0].MediaId!.Value, token))!.State);
        release.TrySetResult();
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "media-ready", token);
        await fixture.FinishedAsync(image.Message.Id, token);
        await fixture.FinishedAsync(text.Message!.Id, token);
    }

    /// <summary>活跃事件保留旧顺序，新事件使用用户保存的新顺序。</summary>
    [Fact]
    public async Task OrderUpdateDoesNotChangeRunningEventSnapshot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.OnSend = async (id, message, cancellation) =>
        {
            if (message.Segments[0].Text == "hold") await release.Task.WaitAsync(cancellation);
            return new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = id.ToString() };
        };
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("a", "v1", token), RuntimeFixture.Configuration("wait"), token);
        await fixture.Runtime.Plugins.LoadAsync(await fixture.PackageAsync("b", "v2", token), RuntimeFixture.Configuration(), token);
        ReceiveReceipt first = await fixture.Runtime.ReceiveAsync(TestStore.Message("first"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "hold", token);
        HookOrderView order = await fixture.Runtime.Orders.GetAsync(MessageHooks.Received, HandlerKind.Interceptor, token);
        await fixture.Runtime.Orders.SaveAsync(order.HookId, order.Kind, order.Items.Reverse().Select(item => item.Handler.Key).ToImmutableArray(), order.Revision, token);
        release.TrySetResult();
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text!.EndsWith("v2>v1>first", StringComparison.Ordinal), token);
        await fixture.FinishedAsync(first.Message!.Id, token);
        ReceiveReceipt second = await fixture.Runtime.ReceiveAsync(TestStore.Message("second"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text!.EndsWith("v1>v2>second", StringComparison.Ordinal), token);
        await fixture.FinishedAsync(second.Message!.Id, token);
    }

    /// <summary>单并发插件发送之后的监听不反向阻塞发送调用。</summary>
    [Fact]
    public async Task AfterSentListenerDoesNotDeadlockSingleConcurrencyPlugin()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("sample", "v1", token, concurrency: 1);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("after-sent"), token);
        ReceiveReceipt receipt = await fixture.Runtime.ReceiveAsync(TestStore.Message("hello"), token);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "after:v1:v1>hello", token);
        await fixture.FinishedAsync(receipt.Message!.Id, token);
        Assert.Equal(2, fixture.Transport.Requests.Count);
    }

    /// <summary>真实写入失败必须阻止外部传输调用。</summary>
    [Fact]
    public async Task FailedIntentWriteNeverCallsTransport()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await ExecuteSqlAsync(fixture, "CREATE TRIGGER fail_intent BEFORE INSERT ON Messages WHEN NEW.Direction=1 BEGIN SELECT RAISE(ABORT, 'synthetic'); END;", token);
        SendResult result = await fixture.Runtime.Sender.SendAsync(new OutgoingMessage
        { Address = TestStore.Address(), Segments = [new MessageSegment { Kind = SegmentKind.Text, Text = "test" }] }, token);
        Assert.Equal(DeliveryState.Failed, result.State);
        Assert.False(result.Recorded);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>平台确认后的写入失败仍保留确认语义，不产生第二次传输。</summary>
    [Fact]
    public async Task FailedReceiptWriteDoesNotResendConfirmedMessage()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await ExecuteSqlAsync(fixture, $"CREATE TRIGGER fail_receipt BEFORE UPDATE OF Delivery ON Messages WHEN NEW.Delivery={(int)DeliveryState.Confirmed} BEGIN SELECT RAISE(ABORT, 'synthetic'); END;", token);
        SendResult result = await fixture.Runtime.Sender.SendAsync(new OutgoingMessage
        { Address = TestStore.Address(), Segments = [new MessageSegment { Kind = SegmentKind.Text, Text = "test" }] }, token);
        Assert.Equal(DeliveryState.Confirmed, result.State);
        Assert.False(result.Recorded);
        Assert.Single(fixture.Transport.Requests);
        Assert.Contains("记录失败", result.Error);
    }

    /// <summary>仅在独立测试数据库中建立故障触发器，不修改生产模型。</summary>
    private static async Task ExecuteSqlAsync(RuntimeFixture fixture, string sql, CancellationToken token)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "history.db"), ForeignKeys = true }.ToString());
        await connection.OpenAsync(token);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }
}
