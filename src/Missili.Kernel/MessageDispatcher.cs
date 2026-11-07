using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>执行消息传播规则，将插件异常限制在单个调用内。</summary>
public sealed class MessageDispatcher
{
    private readonly IHandlerRuntime _plugins;
    private readonly HookOrderService _orders;
    private readonly IInvocationScheduler _scheduler;
    private readonly IMessageSender _sender;
    private readonly ILogger _logger;

    /// <summary>组合目录、编排和调度，实际委托仍由 Nexus 所有。</summary>
    public MessageDispatcher(IHandlerRuntime plugins, HookOrderService orders, IInvocationScheduler scheduler, IMessageSender sender, ILogger<MessageDispatcher> logger)
    { _plugins = plugins; _orders = orders; _scheduler = scheduler; _sender = sender; _logger = logger; }

    /// <summary>一次捕获顺序，串行拦截后独立调度监听，不等待媒体准备。</summary>
    public async Task DispatchAsync(MessageSnapshot original, CancellationToken cancellationToken)
    {
        string hook = original.Direction == MessageDirection.Incoming ? MessageHooks.Received : MessageHooks.Sent;
        ImmutableArray<HookOrderView> snapshot = await _orders.CaptureAsync(hook, cancellationToken).ConfigureAwait(false);
        MessageContext context = new() { OriginalMessage = original, CurrentContent = original.Segments };
        if (original.Direction == MessageDirection.Incoming)
        {
            foreach (HookOrderItem item in snapshot.Single(view => view.Kind == HandlerKind.Interceptor).Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Invocation result = await (await ScheduleAsync(item.Handler, context, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                if (result.Result?.Outcome == HandlerOutcome.Handled) return;
                if (result.Result?.Outcome == HandlerOutcome.Replace) context = context with { CurrentContent = result.Result.Content };
            }
            foreach (HookOrderItem item in snapshot.Single(view => view.Kind == HandlerKind.Command).Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CommandMatch? command = MessageText.MatchCommand(item.Handler.CommandPath, context.CurrentContent);
                if (command is null) continue;
                Invocation result = await (await ScheduleAsync(item.Handler, context with { Command = command }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                if (result.Unavailable)
                {
                    await _sender.SendAsync(OutgoingBatch.Single(original.Address, [new MessageSegment { Kind = SegmentTypes.Text, Text = "插件正在重载或已停用，暂不可用。" }]) with { Purpose = "status", TriggerMessageId = original.Id }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (result.Matched) return;
            }
        }
        List<Task<Invocation>> pending = [];
        foreach (HookOrderItem item in snapshot.Single(view => view.Kind == HandlerKind.Listener).Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Handler.Enabled) continue;
            // 先取得有界排队容量，避免为所有积压监听预建等待任务。
            Task<Invocation> invocation = await ScheduleAsync(item.Handler, context, cancellationToken).ConfigureAwait(false);
            pending.RemoveAll(task => task.IsCompletedSuccessfully);
            pending.Add(invocation);
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>取得代次资格后排队，完成包装负责归还资格与取消源。</summary>
    private async Task<Task<Invocation>> ScheduleAsync(HandlerDescriptor descriptor, MessageContext message, CancellationToken hostToken)
    {
        IHandlerLease? lease = _plugins.TryAcquire(descriptor);
        if (lease is null) return Task.FromResult(new Invocation { Unavailable = true });
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, lease.LifetimeToken);
        try
        {
            Task<Invocation> execution = await _scheduler.EnqueueAsync(descriptor.Key.PluginId, descriptor.MaxConcurrency, Invoke, linked.Token).ConfigureAwait(false);
            return FinishAsync(execution, lease, linked, hostToken);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lease.Dispose();
            linked.Dispose();
            hostToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Invocation { Unavailable = true });
        }
        catch { lease.Dispose(); linked.Dispose(); throw; }

        /// <summary>捕获插件异常并保留是否已经完成命令接管，避免失败后重复分发。</summary>
        async Task<Invocation> Invoke(CancellationToken token)
        {
            bool matched = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (descriptor.Kind == HandlerKind.Command)
                {
                    if (!lease.Matches(message)) return new Invocation();
                    matched = true;
                }
                HandlerResult result = await lease.InvokeAsync(message, token).ConfigureAwait(false);
                if (!Enum.IsDefined(result.Outcome)) throw new InvalidOperationException("处理器返回非法传播结果。");
                if (result.Outcome == HandlerOutcome.Replace) ContentValidation.Validate(result.Content);
                return new Invocation { Result = result, Matched = matched };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                hostToken.ThrowIfCancellationRequested();
                return new Invocation { Matched = matched, Unavailable = !matched };
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "插件 {PluginId} 处理器 {HandlerId} 处理消息 {MessageId} 失败，继续适用的后续传播",
                    descriptor.Key.PluginId, descriptor.Key.HandlerId, message.OriginalMessage.Id);
                return new Invocation { Matched = matched };
            }
        }
    }

    /// <summary>调用真实结束后才释放 Nexus 资格，不因排队取消丢失清理。</summary>
    private static async Task<Invocation> FinishAsync(Task<Invocation> execution, IHandlerLease lease, CancellationTokenSource linked, CancellationToken hostToken)
    {
        try { return await execution.ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            hostToken.ThrowIfCancellationRequested();
            return new Invocation { Unavailable = true };
        }
        finally { lease.Dispose(); linked.Dispose(); }
    }

    /// <summary>纯数据执行结果，不将插件异常传入长期任务或状态缓存。</summary>
    private sealed record Invocation
    {
        public HandlerResult? Result { get; init; }
        public bool Matched { get; init; }
        public bool Unavailable { get; init; }
    }
}
