using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Kernel;
using Xunit;

namespace Missili.Tests;

/// <summary>使用真实 HTTP 客户端、回环服务器及 SQLite 验证归档生命周期。</summary>
public sealed class MediaTests
{
    /// <summary>已清理引用不能因共享原件仍存在而重新开放，也不会触发下载。</summary>
    [Fact]
    public async Task CleanedReferenceRemainsUnavailableWhileSharedBlobExists()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        int requests = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await context.Response.Body.WriteAsync(new byte[] { 9, 8 }, context.RequestAborted);
        }, token);
        ReceiveReceipt first = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress, "same"), TestStore.Now, fixture.RunId, token);
        ReceiveReceipt second = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress, "same"), TestStore.Now, fixture.RunId, token);
        Guid firstId = first.Message!.Segments[0].MediaIds[0];
        Guid secondId = second.Message!.Segments[0].MediaIds[0];
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using (MediaReadResult one = await archive.OpenReadAsync(firstId, token).WaitAsync(TimeSpan.FromSeconds(10), token)) Assert.NotNull(one.Content);
        await using (MediaReadResult two = await archive.OpenReadAsync(secondId, token).WaitAsync(TimeSpan.FromSeconds(10), token)) Assert.NotNull(two.Content);
        using (Microsoft.Data.Sqlite.SqliteConnection connection = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = fixture.DatabasePath, ForeignKeys = true }.ToString()))
        {
            await connection.OpenAsync(token);
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE MediaReferences SET State=$state WHERE Id=$id";
            command.Parameters.AddWithValue("$state", (int)MediaState.Cleaned);
            command.Parameters.AddWithValue("$id", firstId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        }
        await using MediaReadResult cleaned = await archive.OpenReadAsync(firstId, token);
        await using MediaReadResult remaining = await archive.OpenReadAsync(secondId, token);
        Assert.Equal(MediaState.Cleaned, cleaned.Status.State);
        Assert.Null(cleaned.Content);
        Assert.NotNull(remaining.Content);
        Assert.Equal(1, requests);
    }

    /// <summary>原件暂存路径不可写时只失败该引用，其他归档仍可继续。</summary>
    [Fact]
    public async Task FileWriteFailureDoesNotActivateOrStopOtherMedia()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using TestHttpServer server = await TestHttpServer.StartAsync(context =>
            context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }, context.RequestAborted).AsTask(), token);
        ReceiveReceipt failed = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        Guid failedId = failed.Message!.Segments[0].MediaIds[0];
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "media", "staging", failedId.ToString("N") + ".part"));
        ReceiveReceipt valid = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult failure = await archive.OpenReadAsync(failedId, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        await using MediaReadResult success = await archive.OpenReadAsync(valid.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Failed, failure.Status.State);
        Assert.Null(failure.Content);
        Assert.Equal(MediaState.Available, success.Status.State);
        Assert.True(archive.IsHealthy);
    }

    /// <summary>主机关闭结束实际读取，留下的未完成归档可由下一次运行恢复。</summary>
    [Fact]
    public async Task HostStopReleasesNetworkAndNextRunResumesMediaOnly()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                await context.Response.Body.WriteAsync(new byte[] { 1 }, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            else await context.Response.Body.WriteAsync(new byte[] { 2, 3 }, context.RequestAborted);
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        Guid id = message.Message!.Segments[0].MediaIds[0];
        await using (MediaArchive first = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance))
        {
            await first.StartAsync(token);
            Task<MediaReadResult> waiting = first.OpenReadAsync(id, token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await first.StopAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        await fixture.Store.InitializeAsync(Guid.NewGuid(), token);
        await using MediaArchive next = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await next.StartAsync(token);
        await using MediaReadResult result = await next.OpenReadAsync(id, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Available, result.Status.State);
        Assert.Equal(2, requests);
        Assert.Equal(ProcessingState.Interrupted, (await fixture.Store.GetAsync(message.Message.Id, token))!.Processing);
    }

    /// <summary>连接阶段包括尚未完成的 TLS 握手，测试不关闭证书验证。</summary>
    [Fact]
    public async Task ConnectionHandshakeHasBoundedTimeout()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        fixture.Options.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        fixture.Options.HeadersTimeout = TimeSpan.FromSeconds(10);
        System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource serverStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task connection = Task.Run(async () =>
        {
            using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync(serverStop.Token);
            await release.Task.WaitAsync(serverStop.Token);
        }, serverStop.Token);
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(new Uri($"https://127.0.0.1:{port}/media")), TestStore.Now, fixture.RunId, token);
            await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
            await archive.StartAsync(token);
            await using MediaReadResult result = await archive.OpenReadAsync(message.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(MediaState.Failed, result.Status.State);
        }
        finally
        {
            release.TrySetResult();
            await serverStop.CancelAsync();
            listener.Stop();
            try { await connection; }
            catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
        }
    }

    /// <summary>只等待响应头时也会按期限失败，而不是永久占用下载工作。</summary>
    [Fact]
    public async Task MissingHeadersFailWithinDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        fixture.Options.HeadersTimeout = TimeSpan.FromMilliseconds(200);
        await using TestHttpServer server = await TestHttpServer.StartAsync(context => Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted), token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult result = await archive.OpenReadAsync(message.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Failed, result.Status.State);
        Assert.Null(result.Content);
    }

    /// <summary>声明长度与实际传输不一致时不激活部分文件。</summary>
    [Fact]
    public async Task TruncatedResponseDoesNotActivatePartialFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync(new byte[] { 1, 2 }, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            context.Abort();
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult result = await archive.OpenReadAsync(message.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Failed, result.Status.State);
        Assert.Null(result.Content);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.DirectoryPath, "media", "objects"), "*", SearchOption.AllDirectories));
    }

    /// <summary>实际持续超过一分钟的正常传输不会被固定整包时限中断。</summary>
    [Fact]
    [Trait("Category", "LongRunning")]
    public async Task HealthyBodyContinuesBeyondOneMinute()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        fixture.Options.ReadIdleTimeout = TimeSpan.FromSeconds(10);
        byte[] chunk = new byte[32 * 1024];
        Array.Fill(chunk, (byte)7);
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            context.Response.ContentLength = chunk.Length * 66L;
            // 延迟是合成网络传输的一部分，测试最终断言实际完成与字节数。
            for (int index = 0; index < 66; index++)
            {
                if (index > 0) await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult result = await archive.OpenReadAsync(message.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(100), token);
        Assert.Equal(MediaState.Available, result.Status.State);
        Assert.Equal(chunk.Length * 66L, result.Status.Length);
    }

    /// <summary>成功文件按内容共享，但不同消息引用和读取流保持独立。</summary>
    [Fact]
    public async Task ArchiveDeduplicatesContentAndReturnsIndependentStreams()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        byte[] bytes = [1, 2, 3, 4, 5];
        int requests = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }, token);
        ReceiveReceipt first = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress, "stable"), TestStore.Now, fixture.RunId, token);
        ReceiveReceipt second = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress, "stable"), TestStore.Now, fixture.RunId, token);
        Guid firstId = first.Message!.Segments[0].MediaIds[0];
        Guid secondId = second.Message!.Segments[0].MediaIds[0];
        Assert.NotEqual(firstId, secondId);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult a = await archive.OpenReadAsync(firstId, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        await using MediaReadResult b = await archive.OpenReadAsync(secondId, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.Equal(a.Status.ContentKey, b.Status.ContentKey);
        Assert.NotSame(a.Content, b.Content);
        Assert.Equal(1, a.Content!.ReadByte());
        Assert.Equal(1, b.Content!.ReadByte());
        Assert.Equal(1, requests);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.DirectoryPath, "media", "objects"), "*", SearchOption.AllDirectories));
    }

    /// <summary>取消一个插件等待只释放其等待，另一个仍可取得原件。</summary>
    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedDownload()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(new byte[] { 7, 8 }, context.RequestAborted);
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        Guid id = message.Message!.Segments[0].MediaIds[0];
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<MediaReadResult> cancelled = archive.OpenReadAsync(id, caller.Token);
        Task<MediaReadResult> continuing = archive.OpenReadAsync(id, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(continuing.IsCompleted);
        release.TrySetResult();
        await using MediaReadResult result = await continuing.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.NotNull(result.Content);
    }

    /// <summary>HTTP 错误持久化为失败，重复读取不自动重新下载。</summary>
    [Fact]
    public async Task HttpFailureDoesNotRetryOnRead()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        int requests = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        Guid id = message.Message!.Segments[0].MediaIds[0];
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult first = await archive.OpenReadAsync(id, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        await using MediaReadResult second = await archive.OpenReadAsync(id, token);
        Assert.Equal(MediaState.Failed, first.Status.State);
        Assert.NotNull(first.Error);
        Assert.Null(second.Content);
        Assert.Equal(1, requests);
        Assert.NotNull(await fixture.Store.GetAsync(message.Message.Id, token));
    }

    /// <summary>响应头之后的断流也有期限，不依赖 HttpClient 的响应头超时。</summary>
    [Fact]
    public async Task StalledBodyFailsWithinReadDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        fixture.Options.ReadIdleTimeout = TimeSpan.FromMilliseconds(150);
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            await context.Response.Body.WriteAsync(new byte[] { 1 }, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        }, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(server.BaseAddress), TestStore.Now, fixture.RunId, token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult result = await archive.OpenReadAsync(message.Message!.Segments[0].MediaIds[0], token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Failed, result.Status.State);
        Assert.Null(result.Content);
    }

    /// <summary>模拟文件已写完但数据库尚未激活的中断，恢复无需再次访问网络。</summary>
    [Fact]
    public async Task FinalizingRecordRecoversWithoutAnotherDownload()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(new Uri("http://127.0.0.1:1/unreachable")), TestStore.Now, fixture.RunId, token);
        Guid id = message.Message!.Segments[0].MediaIds[0];
        await fixture.Store.ClaimMediaAsync(1, token);
        string temporary = "media/staging/" + id.ToString("N") + ".part";
        string target = "media/objects/ab/recorded-object";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.DirectoryPath, temporary))!);
        await File.WriteAllBytesAsync(Path.Combine(fixture.DirectoryPath, temporary), [1, 2, 3], token);
        await fixture.Store.SetMediaFinalizingAsync(id, new string('a', 64), 3, temporary, target, token);
        await fixture.Store.InitializeAsync(Guid.NewGuid(), token);
        await using MediaArchive archive = new(fixture.Store, fixture.Options, NullLogger<MediaArchive>.Instance);
        await archive.StartAsync(token);
        await using MediaReadResult result = await archive.OpenReadAsync(id, token).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(MediaState.Available, result.Status.State);
        Assert.NotNull(result.Content);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, temporary)));
    }
}
