using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>独立平台事件监听采用既有公平调度、顺序快照和代次取消。</summary>
public sealed class PlatformEventDispatcher
{
    private readonly IHandlerRuntime _plugins;
    private readonly HookOrderService _orders;
    private readonly IInvocationScheduler _scheduler;
    private readonly ILogger _logger;

    /// <summary>组合宿主服务，不缓存插件委托。</summary>
    public PlatformEventDispatcher(IHandlerRuntime plugins, HookOrderService orders, IInvocationScheduler scheduler, ILogger logger)
    { _plugins = plugins; _orders = orders; _scheduler = scheduler; _logger = logger; }

    /// <summary>持久化后的事件按入口快照调度，单个监听异常不影响其他监听。</summary>
    public async Task DispatchAsync(PlatformEventSnapshot platformEvent, CancellationToken token)
    {
        HookOrderView order = await _orders.GetAsync(MessageHooks.PlatformEvent, HandlerKind.Listener, token).ConfigureAwait(false);
        List<Task> pending = [];
        foreach (HookOrderItem item in order.Items)
        {
            token.ThrowIfCancellationRequested();
            IHandlerLease? lease = _plugins.TryAcquire(item.Handler);
            if (lease is null) continue;
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
            try
            {
                Task<bool> execution = await _scheduler.EnqueueAsync(item.Handler.Key.PluginId, item.Handler.MaxConcurrency,
                    Invoke, linked.Token).ConfigureAwait(false);
                pending.RemoveAll(task => task.IsCompletedSuccessfully);
                pending.Add(FinishAsync(execution, lease, linked, token));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            { lease.Dispose(); linked.Dispose(); token.ThrowIfCancellationRequested(); }
            catch { lease.Dispose(); linked.Dispose(); throw; }

            /// <summary>记录该监听的失败；没有取消请求的取消异常仍视为业务异常。</summary>
            async Task<bool> Invoke(CancellationToken cancellation)
            {
                try { await lease.InvokeEventAsync(platformEvent, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "插件 {PluginId} 处理器 {HandlerId} 处理平台事件 {EventId} 失败",
                        item.Handler.Key.PluginId, item.Handler.Key.HandlerId, platformEvent.Id);
                }
                return true;
            }
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>实际调用结束后归还代次资格，排队取消也必须释放。</summary>
    private static async Task FinishAsync(Task execution, IHandlerLease lease, CancellationTokenSource linked, CancellationToken hostToken)
    {
        try { await execution.ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { hostToken.ThrowIfCancellationRequested(); }
        finally { lease.Dispose(); linked.Dispose(); }
    }
}
