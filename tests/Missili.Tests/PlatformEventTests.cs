using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>事件使用真实编译插件、存储与调度，不通过模拟分发跳过生命周期。</summary>
public sealed class PlatformEventTests
{
    /// <summary>故障监听不影响其他插件，回执之前已落库，通知不会进入聊天监听。</summary>
    [Fact]
    public async Task EventIsPersistedBeforeIsolatedListenersRun()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string failed = await fixture.PackageAsync("failed", "v1", token);
        string healthy = await fixture.PackageAsync("healthy", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(failed, RuntimeFixture.Configuration("event-throw"), token);
        await fixture.Runtime.Plugins.LoadAsync(healthy, RuntimeFixture.Configuration("event"), token);
        HookOrderView order = await fixture.Runtime.Orders.GetAsync(MessageHooks.PlatformEvent, HandlerKind.Listener, token);
        Assert.Equal(2, order.Items.Length);
        await fixture.Runtime.Orders.SaveAsync(MessageHooks.PlatformEvent, HandlerKind.Listener,
            [order.Items[1].Handler.Key, order.Items[0].Handler.Key], order.Revision, token);
        EventReceipt receipt = await fixture.Runtime.ReceiveEventAsync(Input(), token);
        Assert.NotNull(receipt.Event);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "event:notify", token);
        Assert.Single(fixture.Transport.Requests);
        Assert.Single((await fixture.Runtime.Events.QueryEventsAsync(new EventQuery { Type = "notify" }, token)).Events);
        Assert.All((await fixture.Runtime.History.QueryAsync(TestStore.Query(), token)).Messages,
            message => Assert.Equal(MessageDirection.Outgoing, message.Direction));
    }

    /// <summary>具有可靠键的事件只分发一次，已保存的原载荷保持可查询。</summary>
    [Fact]
    public async Task ReliableEventKeyPreventsDuplicateDispatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        string package = await fixture.PackageAsync("event", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration("event"), token);
        IncomingPlatformEvent input = Input() with { DeduplicationKey = "request:unique" };
        EventReceipt first = await fixture.Runtime.ReceiveEventAsync(input, token);
        EventReceipt second = await fixture.Runtime.ReceiveEventAsync(input, token);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Event!.Id, second.Event!.Id);
        await fixture.Transport.WaitForAsync(message => message.Segments[0].Text == "event:notify", token);
        await fixture.Runtime.StopAsync(token);
        Assert.Single(fixture.Transport.Requests);
    }

    /// <summary>构造合成群通知，不含真实账号身份。</summary>
    private static IncomingPlatformEvent Input() => new()
    {
        AdapterId = "test",
        PlatformId = "synthetic",
        BotAccountId = "bot",
        Type = "notify",
        Category = PlatformEventCategory.Notice,
        ConversationKind = "group",
        ConversationId = "room",
        ActorId = "user"
    };
}
