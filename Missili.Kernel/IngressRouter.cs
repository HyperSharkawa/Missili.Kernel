using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>组装时绑定唯一运行时入口，使插件服务不依赖具体内核类型。</summary>
public sealed class IngressRouter : IMessageIngress, IPlatformEventIngress
{
    private InfrastructureRuntime? _runtime;
    /// <summary>组装完成后只绑定一次，不允许插件替换宿主接收入口。</summary>
    public void Bind(InfrastructureRuntime runtime)
    {
        if (Interlocked.CompareExchange(ref _runtime, runtime, null) is not null) throw new InvalidOperationException("接收入口已绑定。");
    }
    /// <summary>将共享消息快照交给宿主有界队列。</summary>
    public Task<ReceiveReceipt> ReceiveAsync(IncomingMessage message, CancellationToken cancellationToken = default) =>
        (_runtime ?? throw new InvalidOperationException("接收入口尚未绑定。")).ReceiveAsync(message, cancellationToken);
    /// <summary>将平台事件交给相同的宿主持久化边界。</summary>
    public Task<EventReceipt> ReceiveEventAsync(IncomingPlatformEvent platformEvent, CancellationToken cancellationToken = default) =>
        (_runtime ?? throw new InvalidOperationException("接收入口尚未绑定。")).ReceiveEventAsync(platformEvent, cancellationToken);
}
