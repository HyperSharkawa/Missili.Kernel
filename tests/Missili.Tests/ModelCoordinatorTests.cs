using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Models;
using Missili.Persistence;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 SDK、网络和 SQLite 验证候选策略、取消和失败详情。</summary>
public sealed class ModelCoordinatorTests
{
    /// <summary>失败候选按轮次有序执行，SDK 不增加隐藏重试。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidatesUseExactOrderAndPersistEveryFailedAttempt(bool succeedSecond)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConcurrentQueue<string> calls = new();
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: token);
            string model = request.RootElement.GetProperty("model").GetString()!; calls.Enqueue(model);
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = succeedSecond && model == "B" ? 200 : 500;
            await context.Response.WriteAsync(context.Response.StatusCode == 200 ? Success : """{"error":{"message":"synthetic failure detail","type":"server_error"}}""", token);
        }, token);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await LoadAsync(fixture, token);
        string variable = "MISSILI_MODEL_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-not-a-real-key");
        try
        {
            await using ModelConfigurationService configuration = new(fixture.Root, 8, NullLogger.Instance, TimeProvider.System);
            configuration.Publish(ModelConfigurationTests.Configuration(server.BaseAddress, variable));
            await using ModelCoordinator coordinator = new((IModelStore)fixture.Runtime.History, fixture.Runtime.ModelProviders, new ModelOptions(), fixture.Root, Guid.NewGuid(), NullLogger.Instance, TimeProvider.System);
            ModelRequest request = Request(configuration.Resolve(TestStore.Address()));
            ModelResult result = await coordinator.GenerateAsync(request, cancellationToken: token);
            Assert.Equal(succeedSecond ? ModelCallState.Succeeded : ModelCallState.Failed, result.State);
            Assert.Equal(succeedSecond ? new[] { "A", "B" } : new[] { "A", "B", "A", "B" }, calls.ToArray());
            ModelCallRecord saved = (await coordinator.GetCallAsync(request.Id, token))!;
            Assert.Equal(calls.Count, saved.Attempts.Length);
            foreach (ModelAttemptSummary attempt in saved.Attempts.Where(item => item.State == ModelCallState.Failed))
            {
                await using ModelDetailRead detail = (await coordinator.OpenDetailAsync(attempt.Id, token))!;
                using ZipArchive zip = new(detail.Content, ZipArchiveMode.Read, leaveOpen: true);
                using StreamReader response = new(zip.GetEntry("response.bin")!.Open());
                Assert.Contains("synthetic failure detail", await response.ReadToEndAsync(token));
                using StreamReader input = new(zip.GetEntry("request.json")!.Open());
                string payload = await input.ReadToEndAsync(token);
                Assert.Contains("model", payload);
                Assert.DoesNotContain("synthetic-not-a-real-key", payload);
            }
            if (succeedSecond) Assert.Null(saved.Attempts[^1].DetailId);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>取消流后保留已接收文字，取消不会向备用模型发起请求。</summary>
    [Fact]
    public async Task ExplicitCancellationKeepsPartialTextWithoutFallback()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls); context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Chunk("已收到的部分", false), token);
            await context.Response.Body.FlushAsync(token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        }, token);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await LoadAsync(fixture, token);
        string variable = "MISSILI_MODEL_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-not-a-real-key");
        try
        {
            await using ModelConfigurationService configuration = new(fixture.Root, 8, NullLogger.Instance, TimeProvider.System);
            configuration.Publish(ModelConfigurationTests.Configuration(server.BaseAddress, variable));
            await using ModelCoordinator coordinator = new((IModelStore)fixture.Runtime.History, fixture.Runtime.ModelProviders, new ModelOptions(), fixture.Root, Guid.NewGuid(), NullLogger.Instance, TimeProvider.System);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancelOnText sink = new(cancellation);
            ModelResult result = await coordinator.GenerateAsync(Request(configuration.Resolve(TestStore.Address())) with { Stream = true }, sink, cancellation.Token);
            Assert.Equal(ModelCallState.Cancelled, result.State);
            Assert.Contains("已收到的部分", result.Text);
            Assert.False(result.Complete);
            Assert.Equal(1, calls);
            Assert.Single(result.Attempts);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>必要调用记录写入失败时不执行网络请求。</summary>
    [Fact]
    public async Task MetadataFailurePreventsNetworkCall()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(context => { Interlocked.Increment(ref calls); return context.Response.WriteAsync(Success, token); }, token);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        await LoadAsync(fixture, token);
        await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "history.db"), ForeignKeys = true }.ToString()).Options);
        await database.Database.ExecuteSqlRawAsync("DROP TABLE ModelCalls", token);
        await using ModelConfigurationService configuration = new(fixture.Root, 8, NullLogger.Instance, TimeProvider.System);
        configuration.Publish(ModelConfigurationTests.Configuration(server.BaseAddress));
        await using ModelCoordinator coordinator = new((IModelStore)fixture.Runtime.History, fixture.Runtime.ModelProviders, new ModelOptions(), fixture.Root, Guid.NewGuid(), NullLogger.Instance, TimeProvider.System);
        ModelResult result = await coordinator.GenerateAsync(Request(configuration.Resolve(TestStore.Address())), cancellationToken: token);
        Assert.Equal(ModelCallState.RecordingFailed, result.State);
        Assert.Equal(0, calls);
    }

    /// <summary>从实际构建目录加载提供商 DLL，不建立静态程序集引用。</summary>
    private static async Task LoadAsync(RuntimeFixture fixture, CancellationToken token)
    {
        PluginStatus status = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Providers/openai"), RuntimeFixture.Configuration(), token);
        Assert.Equal(PluginState.Active, status.State);
    }

    /// <summary>协议请求使用固定快照和合成文本，不选择任何真实模型服务。</summary>
    private static ModelRequest Request(ConversationProfileSnapshot profile) => new()
    { Purpose = "reply", Profile = profile, Candidates = profile.Profile.ReplyModels, Stream = false, Messages = [new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("合成请求")] }] };

    private const string Success = """{"id":"chat-1","object":"chat.completion","created":1,"model":"B","choices":[{"index":0,"message":{"role":"assistant","content":"完整回复"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";

    /// <summary>直接依据协议创建测试帧，不复用生产编码器。</summary>
    private static string Chunk(string text, bool complete) => "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"A\",\"choices\":[{\"index\":0,\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "},\"finish_reason\":" + (complete ? "\"stop\"" : "null") + "}]}\n\n";

    /// <summary>在真实增量到达后请求取消，以信号控制顺序而非固定休眠。</summary>
    private sealed class CancelOnText : IModelUpdateSink
    {
        private readonly CancellationTokenSource _source;
        /// <summary>绑定本测试拥有的调用取消源。</summary>
        public CancelOnText(CancellationTokenSource source) => _source = source;
        /// <summary>接到正文后取消调用，保留该次已经收到的文字。</summary>
        public Task OnUpdateAsync(ModelUpdate update, CancellationToken cancellationToken)
        { if (update.Text is not null) _source.Cancel(); return Task.CompletedTask; }
    }
}
