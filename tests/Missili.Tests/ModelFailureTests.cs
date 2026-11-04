using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;
using Missili.Persistence;
using Missili.Tools.ModelMock;
using Xunit;

namespace Missili.Tests;

/// <summary>真实协议故障不能污染备用输出，也不能把本地故障变成重复模型请求。</summary>
public sealed class ModelFailureTests
{
    /// <summary>损坏的最终工具参数属于协议失败，不能在错误收尾时再次抛出而阻断候选策略。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedToolArgumentsUseFallback(bool streaming)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using JsonDocument malformed = JsonDocument.Parse("""{"id":"bad","object":"response","created_at":1,"status":"completed","model":"A","output":[{"type":"function_call","id":"function-one","call_id":"call-one","name":"tool","arguments":"{broken","status":"completed"}]}""");
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps =
            [new ModelMockStep { Response = malformed.RootElement.Clone(), Events = ["event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":" + malformed.RootElement.GetRawText() + "}\n\n"] },
             new ModelMockStep { Text = "协议失败后的独立结果" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_FAILURE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable, ModelProtocol.Responses));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.Responses, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(Request(fixture) with { Stream = streaming }, cancellationToken: token);
            Assert.True(result.Complete, result.Error); Assert.Equal("协议失败后的独立结果", result.Text);
            Assert.Equal("Protocol", result.Attempts[0].ErrorKind); Assert.Equal(2, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>UTF-8 字节分片与提前 EOF 保留失败证据，下一候选从独立结果开始。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task TruncatedStreamPreservesEvidenceWithoutJoiningFallback(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            StrictSteps = true,
            Steps =
            [new ModelMockStep { Text = "失败片段😀", FragmentBytes = 1, Truncated = true }, new ModelMockStep { Text = "完整的备用结果", FragmentBytes = 1 }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_FAILURE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable, protocol));
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelRequest request = Request(fixture);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(request, cancellationToken: token);
            Assert.True(result.Complete, result.Error); Assert.Equal("完整的备用结果", result.Text);
            Assert.Equal(new[] { "A", "B" }, mock.Requests.Select(item => item.Model));
            ModelAttemptSummary failed = result.Attempts[0];
            Assert.True(failed.HasPartialResult); Assert.Equal(ModelCallState.Failed, failed.State);
            await using ModelDetailRead detail = (await fixture.Runtime.Models.Calls.OpenDetailAsync(failed.Id, token))!;
            using ZipArchive archive = new(detail.Content, ZipArchiveMode.Read);
            await using Stream outcome = archive.GetEntry("outcome.json")!.Open();
            using JsonDocument document = await JsonDocument.ParseAsync(outcome, cancellationToken: token);
            Assert.Contains("失败片段", document.RootElement.GetProperty("Result").GetProperty("Content")[0].GetProperty("Text").GetString());
            Assert.False(document.RootElement.GetProperty("Result").GetProperty("Complete").GetBoolean());
            Assert.Equal(mock.Requests[0].Body!.Value.GetProperty(protocol == ModelProtocol.Responses ? "input" : "messages").GetRawText(),
                mock.Requests[1].Body!.Value.GetProperty(protocol == ModelProtocol.Responses ? "input" : "messages").GetRawText());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>增量消费者故障只结束当前调用，不能尝试备用模型。</summary>
    [Fact]
    public async Task ConsumerFailureDoesNotRetry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario(), "http://127.0.0.1:0", token);
        string variable = "MISSILI_FAILURE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(Request(fixture), new ThrowingSink(), token);
            Assert.Equal("Consumer", Assert.Single(result.Attempts).ErrorKind); Assert.Equal(1, mock.TotalRequests);
            Assert.False(result.Complete); Assert.NotEmpty(result.Text);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>网络取得完整结果后数据库写入失败，返回内容但不会重新请求。</summary>
    [Fact]
    public async Task ResultRecordingFailureRetainsResultAndDoesNotRetry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario(), "http://127.0.0.1:0", token);
        string variable = "MISSILI_FAILURE_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "history.db"), ForeignKeys = true }.ToString()).Options);
            // 只阻止结果更新，建档和网络请求仍走真实生产路径。
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailModelResult BEFORE UPDATE ON ModelCalls BEGIN SELECT RAISE(FAIL, 'synthetic write failure'); END;", token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(Request(fixture), cancellationToken: token);
            Assert.Equal(ModelCallState.RecordingFailed, result.State); Assert.True(result.Complete); Assert.NotEmpty(result.Text);
            Assert.Contains("结果已取得", result.Error); Assert.Equal(1, mock.TotalRequests);
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER FailModelResult", token);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>从当前真实模块取得配置，正文固定以便检查失败后没有删改输入。</summary>
    internal static ModelRequest Request(RuntimeFixture fixture) => new()
    {
        Purpose = "failure.test",
        Profile = fixture.Runtime.Models.Profiles.Resolve(TestStore.Address()),
        Candidates = ["A", "B"],
        Messages = [new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("保持原始输入😀")] }]
    };

    /// <summary>模型目录使用两个顺序候选，禁用额外轮次使请求次数可直接核对。</summary>
    internal static ModelConfiguration Configuration(Uri address, string variable, ModelProtocol protocol)
    {
        ModelConfiguration config = ModelConfigurationTests.Configuration(protocol == ModelProtocol.Anthropic ? address : new Uri(address, "/v1"), variable);
        return config with { Providers = [config.Providers[0] with { Protocol = protocol }], Profiles = [config.Profiles[0] with { Execution = new ModelExecutionOptions { Rounds = 1 } }] };
    }

    /// <summary>模拟业务插件增量消费异常，不持有提供商对象。</summary>
    private sealed class ThrowingSink : IModelUpdateSink
    {
        /// <summary>首次正文到达后明确失败。</summary>
        public Task OnUpdateAsync(ModelUpdate update, CancellationToken cancellationToken) => update.Text is null ? Task.CompletedTask : throw new InvalidOperationException("synthetic consumer");
    }
}
