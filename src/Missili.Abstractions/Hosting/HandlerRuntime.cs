using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Missili.Abstractions;

/// <summary>内核调用资格，持有期间由 Nexus 跟踪，不允许旧代次接单。</summary>
public interface IHandlerLease : IDisposable
{
    CancellationToken LifetimeToken { get; }
    /// <summary>在已取得资格的调用中进行命令匹配。</summary>
    bool Matches(MessageContext message);
    /// <summary>执行已登记处理器，调用者负责捕获插件业务异常。</summary>
    Task<HandlerResult> InvokeAsync(MessageContext message, CancellationToken cancellationToken);
    /// <summary>执行平台事件监听，事件类型与消息上下文保持独立。</summary>
    Task InvokeEventAsync(PlatformEventSnapshot platformEvent, CancellationToken cancellationToken);
}

/// <summary>只供宿主实现使用的插件目录与原子调用入口。</summary>
public interface IHandlerRuntime
{
    /// <summary>生成不含委托的处理器快照。</summary>
    ImmutableArray<HandlerDescriptor> Capture(string hookId, HandlerKind kind);
    /// <summary>原子验证代次和接单状态，成功后登记调用。</summary>
    IHandlerLease? TryAcquire(HandlerDescriptor descriptor);
}
