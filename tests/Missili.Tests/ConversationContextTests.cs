using System.Collections.Immutable;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>上下文投影使用真实历史和配置，原始消息保持不变。</summary>
public sealed class ConversationContextTests
{
    /// <summary>直接模型消费者的处理覆盖跟随规范身份合并；别名不能重复正文或绕过窗口淘汰。</summary>
    [Fact]
    public async Task TriggerAliasUsesCanonicalWindowEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration config = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1"));
        config = config with { Profiles = [config.Profiles[0] with { MaximumMessages = 3, DiscardMessages = 2 }] };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
        IInfrastructureStore store = (IInfrastructureStore)fixture.Runtime.History;
        MessageSnapshot intent = await store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("原始内容")] }, TestStore.Now, Guid.NewGuid(), token);
        MessageSnapshot observed = (await fixture.Runtime.ReceiveAsync(TestStore.Message("原始内容") with
        { Direction = MessageDirection.Outgoing, SenderId = TestStore.Address().BotAccountId, PlatformMessageId = "alias-test" }, token)).Message!;
        await store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "alias-test" }, token);
        Assert.Equal(observed.Id, (await store.GetAsync(intent.Id, token))!.Id);
        MessageContext source = new() { OriginalMessage = intent, CurrentContent = [SegmentValues.Text("处理后的内容")] };
        ConversationContextSnapshot context = await fixture.Runtime.Models.Contexts.CaptureAsync(source, token);
        MessageSnapshot projected = Assert.Single(context.Window.Messages);
        Assert.Equal(observed.Id, projected.Id); Assert.Equal("处理后的内容", projected.Segments[0].Text);
        await fixture.Runtime.ReceiveAsync(TestStore.Message("后续一"), token);
        await fixture.Runtime.ReceiveAsync(TestStore.Message("后续二"), token);
        ConversationContextSnapshot evicted = await fixture.Runtime.Models.Contexts.CaptureAsync(source, token);
        Assert.DoesNotContain(evicted.Window.Messages, message => message.Id == observed.Id || message.Id == intent.Id);
    }

    /// <summary>较早唤醒消息已被窗口淘汰时不补回，直接模型消费者也遵守同一边界。</summary>
    [Fact]
    public async Task BackloggedTriggerIsNotRestoredOutsideWindow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration config = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1"));
        config = config with { Profiles = [config.Profiles[0] with { MaximumMessages = 3, DiscardMessages = 2 }] };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
        MessageSnapshot trigger = (await fixture.Runtime.ReceiveAsync(TestStore.Message("较早的触发消息"), token)).Message!;
        await fixture.Runtime.ReceiveAsync(TestStore.Message("随后消息"), token);
        await fixture.Runtime.ReceiveAsync(TestStore.Message("最新消息"), token);
        ConversationContextSnapshot context = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext { OriginalMessage = trigger, CurrentContent = [SegmentValues.Text("当前请求投影")] }, token);
        Assert.True(context.Window.DiscardedThrough >= trigger.Id);
        Assert.DoesNotContain(context.Window.Messages, item => item.Id == trigger.Id);
        Assert.Equal("最新消息", Assert.Single(context.Window.Messages).Segments[0].Text);
        ConversationWindow stored = await ((IModelStore)fixture.Runtime.History).CaptureWindowAsync(TestStore.Address(), 3, 2, token);
        Assert.DoesNotContain(stored.Messages, item => item.Id == trigger.Id);
    }

    /// <summary>三档撤回策略只影响模型资料，同时当前处理内容只出现一次。</summary>
    [Theory]
    [InlineData(MessageVisibilityPolicy.Placeholder, false, true)]
    [InlineData(MessageVisibilityPolicy.MarkedOriginal, true, true)]
    [InlineData(MessageVisibilityPolicy.Original, true, false)]
    public async Task VisibilityAndCurrentContentDoNotOverwriteHistory(MessageVisibilityPolicy policy, bool originalVisible, bool marked)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration config = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1"));
        config = config with { Profiles = [config.Profiles[0] with { Visibility = policy }] };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
        MessageSnapshot old = (await fixture.Runtime.ReceiveAsync(TestStore.Message("被撤回的原文") with { SenderId = "another-user" }, token)).Message!;
        await ((IInfrastructureStore)fixture.Runtime.History).SetVisibilityAsync(old.Id, true, false, token);
        MessageSnapshot trigger = (await fixture.Runtime.ReceiveAsync(TestStore.Message("原始输入"), token)).Message!;
        await fixture.Runtime.ReceiveAsync(TestStore.Message("另一个会话不应出现", conversation: "other"), token);
        ConversationContextSnapshot snapshot = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext
        { OriginalMessage = trigger, CurrentContent = [SegmentValues.Text("处理后的输入")] }, token);
        ImmutableArray<ModelMessage> projected = await fixture.Runtime.Models.Contexts.ProjectAsync(snapshot, snapshot.Profile.Models["A"], token);
        string text = string.Concat(projected.SelectMany(item => item.Content).Select(item => item.Text));
        Assert.Equal(originalVisible, text.Contains("被撤回的原文", StringComparison.Ordinal));
        Assert.Equal(marked, text.Contains("已撤回", StringComparison.Ordinal));
        Assert.Contains("another-user", text); Assert.Contains("处理后的输入", text);
        Assert.DoesNotContain("另一个会话不应出现", text); Assert.DoesNotContain("原始输入", text);
        Assert.Equal("原始输入", (await fixture.Runtime.History.GetAsync(trigger.Id, token))!.Segments[0].Text);
        Assert.All(projected, message => Assert.Equal("user", message.Role));
        MessageSnapshot later = (await fixture.Runtime.ReceiveAsync(TestStore.Message("快照之后才到达"), token)).Message!;
        Assert.True(later.Id > snapshot.Window.HighWatermark);
        string retained = string.Concat((await fixture.Runtime.Models.Contexts.ProjectAsync(snapshot, snapshot.Profile.Models["A"], token)).SelectMany(item => item.Content).Select(item => item.Text));
        Assert.DoesNotContain("快照之后才到达", retained);
    }

    /// <summary>视频只使用已有标题占位，不调用转述或将视频文件提交给模型。</summary>
    [Fact]
    public async Task VideoAndUnknownSegmentsRemainExplicitContextData()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1")));
        MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message() with
        {
            Segments =
            [new IncomingSegment { Kind = SegmentTypes.Video, Payload = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["title"] = "实际视频标题" }) },
             new IncomingSegment { Kind = "future.card", ParseState = SegmentParseState.Unrecognized, Payload = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["value"] = "扩展原文" }) }]
        }, token)).Message!;
        ConversationContextSnapshot context = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext { OriginalMessage = message, CurrentContent = message.Segments }, token);
        ImmutableArray<ModelMessage> result = await fixture.Runtime.Models.Contexts.ProjectAsync(context, context.Profile.Models["A"], token);
        string text = string.Concat(result.SelectMany(item => item.Content).Select(item => item.Text));
        Assert.Contains("视频", text); Assert.Contains("实际视频标题", text); Assert.Contains("future.card", text);
        Assert.All(result.SelectMany(item => item.Content), block => Assert.Equal(ModelBlockKind.Text, block.Kind));
        Assert.Empty((await fixture.Runtime.Models.Calls.QueryCallsAsync(new ModelCallQuery(), token)).Calls);
    }
}
