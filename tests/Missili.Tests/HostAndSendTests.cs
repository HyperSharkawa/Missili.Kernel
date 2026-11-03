using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Host;
using Missili.Kernel;
using Xunit;

namespace Missili.Tests;

/// <summary>验证真实 Web 宿主和发送结果边界，所有目标均为测试适配器或回环地址。</summary>
public sealed class HostAndSendTests
{
    /// <summary>宿主直接发送也参与关闭跟踪，忽略取消的传输退出前不能关闭存储。</summary>
    [Fact]
    public async Task ShutdownWaitsForDirectHostSendAndRecordsItsActualResult()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.OnSend = async (_, _, cancellation) =>
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new TransportResult { State = DeliveryState.Confirmed };
        };
        Task<SendResult> sending = fixture.Runtime.Sender.SendAsync(Text(), token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Task stopping = fixture.Runtime.StopAsync(token);
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(DeliveryState.Sending, Assert.Single((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages).Delivery);
        }
        finally { release.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), token);
        SendResult result = await sending;
        Assert.Equal(DeliveryState.Confirmed, result.State);
        Assert.True(result.Recorded);
        Assert.Equal(DeliveryState.Cancelled, (await fixture.Runtime.Sender.SendAsync(Text(), token)).State);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>传输明确拒绝或明确未发送时保留其结果，不转换成成功或重试。</summary>
    [Theory]
    [InlineData(DeliveryState.Failed)]
    [InlineData(DeliveryState.Cancelled)]
    public async Task DefinitiveTransportResultIsRecordedOnce(DeliveryState state)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        fixture.Transport.OnSend = (_, _, _) => Task.FromResult(new TransportResult { State = state, Error = "synthetic" });
        SendResult result = await fixture.Runtime.Sender.SendAsync(Text(), token);
        Assert.Equal(state, result.State);
        Assert.True(result.Recorded);
        Assert.Equal(state, (await fixture.Runtime.History.GetAsync(result.MessageId!.Value, token))!.Delivery);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>未注册平台时明确不可用，不能以生产假传输返回成功。</summary>
    [Fact]
    public async Task MissingTransportIsExplicitlyUnavailable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        SendResult result = await fixture.Runtime.Sender.SendAsync(Text() with { Address = TestStore.Address() with { AdapterId = "missing" } }, token);
        Assert.Equal(DeliveryState.Failed, result.State);
        Assert.Contains("尚未注册", result.Error);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>HTTP 宿主只有健康入口，默认没有业务适配器或管理接口。</summary>
    [Fact]
    public async Task HostExposesHealthWithoutBusinessEndpoints()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        WebApplication app = Program.BuildApplication([], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Infrastructure:DataDirectory"] = root });
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        });
        try
        {
            await app.StartAsync(token);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using HttpClient client = new() { BaseAddress = new Uri(address) };
            using HttpResponseMessage live = await client.GetAsync("/health/live", token);
            using HttpResponseMessage ready = await client.GetAsync("/health/ready", token);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("{\"status\":\"ready\"}", await ready.Content.ReadAsStringAsync(token));
            using HttpResponseMessage oldTemplate = await client.GetAsync("/weatherforecast", token);
            using HttpResponseMessage management = await client.GetAsync("/api/plugins", token);
            Assert.Equal(HttpStatusCode.NotFound, oldTemplate.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, management.StatusCode);
            Assert.Empty(app.Services.GetRequiredService<InfrastructureRuntime>().Plugins.GetStatuses());
            await app.StopAsync(token);
        }
        finally
        {
            await app.DisposeAsync();
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "history.db"), ForeignKeys = true }.ToString());
            SqliteConnection.ClearPool(connection);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missili.Tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("测试清理路径越界。");
            Directory.Delete(root, true);
        }
    }

    /// <summary>传输开始后的取消无法证明未发送，结果记录为未知且只调用一次。</summary>
    [Fact]
    public async Task CancellationAfterTransportStartsIsUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.OnSend = async (_, _, cancellation) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new TransportResult { State = DeliveryState.Confirmed };
        };
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<SendResult> sending = fixture.Runtime.Sender.SendAsync(Text(), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await caller.CancelAsync();
        SendResult result = await sending.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(DeliveryState.Unknown, result.State);
        Assert.True(result.Recorded);
        Assert.Single(fixture.Transport.Requests);
        Assert.Equal(DeliveryState.Unknown, (await fixture.Runtime.History.GetAsync(result.MessageId!.Value, token))!.Delivery);
    }

    /// <summary>调用开始前的取消不创建任何外部发送动作。</summary>
    [Fact]
    public async Task PreCancelledSendDoesNotCallTransport()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using CancellationTokenSource caller = new();
        await caller.CancelAsync();
        SendResult result = await fixture.Runtime.Sender.SendAsync(Text(), caller.Token);
        Assert.Equal(DeliveryState.Cancelled, result.State);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>未知发送和未完成插件业务在新运行时中不会重新执行。</summary>
    [Fact]
    public async Task RestartDoesNotReplayUnknownSendsOrInterruptedMessages()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        fixture.Transport.OnSend = (_, _, _) => Task.FromResult(new TransportResult { State = DeliveryState.Unknown, Error = "synthetic" });
        SendResult sent = await fixture.Runtime.Sender.SendAsync(Text(), token);
        await fixture.Runtime.DisposeAsync();
        // 构造旧运行留下的待处理记录，而非通过新运行入口提交。
        await using (Missili.Persistence.SqliteStore store = new(Path.Combine(fixture.Root, "history.db"), fixture.Options))
            await store.AppendAsync(TestStore.Message("old"), TestStore.Now, Guid.NewGuid(), token);
        await using InfrastructureRuntime next = InfrastructureFactory.Create(fixture.Options, NullLoggerFactory.Instance, [fixture.Transport]);
        await next.StartAsync(token);
        string package = await fixture.PackageAsync("sample", "v1", token);
        await next.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token);
        HistoryPage page = await next.History.QueryAsync(TestStore.Query(), token);
        Assert.Equal(DeliveryState.Unknown, page.Messages.Single(message => message.Id == sent.MessageId).Delivery);
        Assert.Equal(ProcessingState.Interrupted, page.Messages.Single(message => message.Direction == MessageDirection.Incoming).Processing);
        await next.StopAsync(token);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>提供统一的纯文本发送输入。</summary>
    private static OutgoingMessage Text() => new()
    { Address = TestStore.Address(), Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = "synthetic" }] };
}
