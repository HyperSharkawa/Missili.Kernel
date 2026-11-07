using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Missili.Nexus;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>以控制信号和虚拟时钟验证模型期限、重试及实际提供商代次。</summary>
public sealed class ModelLifetimeTests
{
    /// <summary>持续输出可以越过多次响应期限，模型模块没有隐含的生成总期限。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task ContinuousOutputHasNoGenerationDeadline(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ObservedClock clock = new(); DateTimeOffset started = clock.GetUtcNow();
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { TextParts = ["一", "二", "三", "四", "五"] } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_LIFETIME_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock, models: ModelFailureTests.Configuration(mock.BaseAddress, variable, protocol));
            await ModelMediaTests.LoadProviderAsync(fixture, protocol, token);
            ModelResult result = await fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, new AdvancingSink(clock), token);
            Assert.True(result.Complete, result.Error); Assert.Equal("一二三四五", result.Text);
            Assert.Equal(TimeSpan.FromMinutes(5), clock.GetUtcNow() - started); Assert.Equal(1, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>响应头和流读取分别计时，超时不依赖固定休眠。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageTimeoutsUseTheConfiguredClock(bool body)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ObservedClock clock = new();
        ModelMockStep step = body ? new ModelMockStep { BeforeEventSignals = ImmutableDictionary<int, string>.Empty.Add(1, "end") }
            : new ModelMockStep { WaitForSignal = "headers" };
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = step }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_LIFETIME_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions);
            config = config with { Profiles = [config.Profiles[0] with { Execution = new ModelExecutionOptions { Rounds = 1, HeadersTimeout = TimeSpan.FromSeconds(11), ReadIdleTimeout = TimeSpan.FromSeconds(13) } }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock, models: config);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            TextSignal sink = new(clock);
            Task<ModelResult> call = fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, sink, token);
            await mock.NextRequestAsync(token);
            if (body) { await sink.Seen.Task.WaitAsync(token); await clock.WaitForTimerAsync(TimeSpan.FromSeconds(13), token); }
            clock.Advance(TimeSpan.FromSeconds(body ? 14 : 12));
            ModelResult result = await call.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(ModelCallState.Failed, result.State); Assert.Single(result.Attempts); Assert.Equal(1, mock.TotalRequests);
            Assert.False(result.Complete); Assert.Equal(body, !string.IsNullOrEmpty(result.Text));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>轮间等待不占网络额度；单候选总计两次且取消不继续下一轮。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundDelayIsBoundedAndCancellable(bool cancel)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ObservedClock clock = new();
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { Status = 500 } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_LIFETIME_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions);
            config = config with { Profiles = [config.Profiles[0] with { Execution = new ModelExecutionOptions() }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, clock: clock, models: config);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<ModelResult> call = fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, cancellationToken: stop.Token);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), token);
            Assert.Equal(1, mock.TotalRequests);
            if (cancel) stop.Cancel(); else clock.Advance(TimeSpan.FromSeconds(2));
            ModelResult result = await call.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(cancel ? ModelCallState.Cancelled : ModelCallState.Failed, result.State);
            Assert.Equal(cancel ? 1 : 2, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>重载只取消旧提供商流，另一提供商继续，旧程序集实际可回收。</summary>
    [Fact]
    public async Task ReloadCancelsOneProviderAndKeepsTheOtherRunning()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        {
            Steps =
            [new ModelMockStep { BeforeEventSignals = ImmutableDictionary<int, string>.Empty.Add(1, "old") }, new ModelMockStep { WaitForSignal = "other" }]
        }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_LIFETIME_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ModelFailureTests.Configuration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions);
            config = config with
            {
                Providers = config.Providers.Add(new ModelProviderDefinition { Id = "other", Protocol = ModelProtocol.Anthropic, BaseUrl = mock.BaseAddress, ApiKeyEnvironmentVariable = variable }),
                Models = [config.Models[0], config.Models[1] with { ProviderId = "other" }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.Anthropic, token);
            TextSignal sink = new();
            Task<ModelResult> old = fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, sink, token);
            await sink.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await mock.NextRequestAsync(token);
            Task<ModelResult> other = fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["B"] }, cancellationToken: token);
            await mock.NextRequestAsync(token);
            PluginStatus reloaded = await fixture.Runtime.Plugins.ReloadAsync("missili.providers.openai", OfficialPluginPackages.GetPath("openai"), RuntimeFixture.Configuration(), token);
            Assert.Equal(PluginState.Active, reloaded.State);
            WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference(reloaded.Id)!;
            ModelResult interrupted = await old;
            Assert.Equal(ModelCallState.Failed, interrupted.State); Assert.NotEmpty(interrupted.Text);
            Assert.Equal("ProviderStopped", Assert.Single(interrupted.Attempts).ErrorKind);
            Assert.False(other.IsCompleted);
            mock.Release("other"); Assert.True((await other).Complete);
            Assert.True((await fixture.Runtime.Models.GenerateAsync(ModelFailureTests.Request(fixture) with { Candidates = ["A"] }, cancellationToken: token)).Complete);
            for (int index = 0; index < 20 && reference.IsAlive; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
            Assert.False(reference.IsAlive);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>在每次真实 SDK 正文增量后推进虚拟时间，避免把长输出测试变成墙钟休眠。</summary>
    private sealed class AdvancingSink : IModelUpdateSink
    {
        private readonly ObservedClock _clock;
        /// <summary>绑定框架和提供商共同使用的时钟。</summary>
        public AdvancingSink(ObservedClock clock) => _clock = clock;
        /// <summary>每段正文代表持续一分钟的输出，下一段继续使用同一次请求。</summary>
        public Task OnUpdateAsync(ModelUpdate update, CancellationToken cancellationToken)
        { if (!string.IsNullOrEmpty(update.Text)) _clock.Advance(TimeSpan.FromMinutes(1)); return Task.CompletedTask; }
    }

    /// <summary>正文到达后通知测试；清空旧计时通知以等待下一次真实读取。</summary>
    private sealed class TextSignal : IModelUpdateSink
    {
        private readonly ObservedClock? _clock;
        public TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>时钟仅用于阶段测试，重载场景不需要时间推进。</summary>
        public TextSignal(ObservedClock? clock = null) => _clock = clock;
        /// <summary>信号表示 SDK 已消费正文，不能用请求发出代替流读取证据。</summary>
        public Task OnUpdateAsync(ModelUpdate update, CancellationToken cancellationToken)
        { if (update.Text is not null) { _clock?.ClearNotifications(); Seen.TrySetResult(); } return Task.CompletedTask; }
    }

    /// <summary>代理正式 FakeTimeProvider，并公开计时器建立信号消除测试调度竞态。</summary>
    private sealed class ObservedClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        /// <summary>UTC 与单调计时共同由受控时钟提供。</summary>
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        /// <summary>使用同一单调时间源计算经过时间。</summary>
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        /// <summary>先登记实际计时器，再通知测试可以推进时间。</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { ITimer timer = _clock.CreateTimer(callback, state, dueTime, period); _timers.Writer.TryWrite(dueTime); return timer; }
        /// <summary>推进模拟时间，不等待墙钟。</summary>
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
        /// <summary>阶段切换时丢弃此前通知，不取消实际计时器。</summary>
        public void ClearNotifications() { while (_timers.Reader.TryRead(out TimeSpan ignored)) { } }
        /// <summary>只等待指定阶段的计时器建立，失败上界由测试令牌控制。</summary>
        public async Task WaitForTimerAsync(TimeSpan expected, CancellationToken token)
        { while (await _timers.Reader.WaitToReadAsync(token)) while (_timers.Reader.TryRead(out TimeSpan value)) if (value == expected) return; }
    }
}
