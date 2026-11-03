using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>保存发送意图后至多调用一次传输；本类没有任何具体平台实现。</summary>
public sealed class MessageSender : IMessageSender, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    private bool _disposed;
    private readonly IInfrastructureStore _store;
    private readonly IReadOnlyDictionary<string, IMessageTransport> _transports;
    private readonly DispatchSignal _signal;
    private readonly Guid _runId;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>组装明确注册的传输实现；空集合表示尚未接入平台。</summary>
    public MessageSender(IInfrastructureStore store, IEnumerable<IMessageTransport> transports, DispatchSignal signal, Guid runId, ILogger<MessageSender> logger, TimeProvider? clock = null)
    {
        _store = store;
        _transports = transports.ToDictionary(item => item.AdapterId, StringComparer.Ordinal);
        _signal = signal;
        _runId = runId;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>意图或发送中状态写入失败时不调用传输；未知结果不自动重发。</summary>
    public async Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken = default)
    {
        message.Address.Validate();
        ContentValidation.Validate(message.Segments);
        lock (_gate)
        {
            if (_closing) return new SendResult { State = DeliveryState.Cancelled, Recorded = false, Error = "宿主已停止接收发送。" };
            _active++;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { return await SendCoreAsync(message, linked.Token).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _active--;
                if (_closing && _active == 0) _drained.TrySetResult();
            }
        }
    }

    /// <summary>在已登记的发送调用内执行一次意图、传输和结果记录。</summary>
    private async Task<SendResult> SendCoreAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new SendResult { State = DeliveryState.Cancelled, Recorded = false };
        MessageSnapshot stored;
        try { stored = await _store.AppendOutgoingAsync(message, _clock.GetUtcNow(), _runId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new SendResult { State = DeliveryState.Cancelled, Recorded = false }; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "发送意图保存失败，未调用传输");
            return new SendResult { State = DeliveryState.Failed, Recorded = false, Error = "发送意图保存失败：" + exception.GetType().Name };
        }
        TransportResult result;
        if (cancellationToken.IsCancellationRequested)
            result = new TransportResult { State = DeliveryState.Cancelled };
        else if (!_transports.TryGetValue(message.Address.AdapterId, out IMessageTransport? transport))
            result = new TransportResult { State = DeliveryState.Failed, Error = "目标适配器尚未注册传输实现。" };
        else
        {
            try { await _store.SetDeliveryAsync(stored.Id, new TransportResult { State = DeliveryState.Sending }, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return await RecordAsync(stored.Id, new TransportResult { State = DeliveryState.Cancelled }).ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogError(exception, "发送 {MessageId} 的执行状态保存失败，未调用传输", stored.Id);
                return new SendResult { MessageId = stored.Id, State = DeliveryState.Failed, Recorded = false, Error = "发送执行状态保存失败，未调用传输。" };
            }
            if (cancellationToken.IsCancellationRequested) result = new TransportResult { State = DeliveryState.Cancelled };
            else
            {
                try
                {
                    result = await transport.SendAsync(stored.Id, message, cancellationToken).ConfigureAwait(false);
                    if (result.State is not (DeliveryState.Confirmed or DeliveryState.Failed or DeliveryState.Unknown or DeliveryState.Cancelled))
                        throw new InvalidOperationException("传输返回了非法的最终状态。");
                }
                catch (Exception exception)
                {
                    // 传输已经进入调用，异常或本地取消都不能证明平台没有执行。
                    _logger.LogWarning(exception, "发送 {MessageId} 未取得确定结果", stored.Id);
                    result = new TransportResult { State = DeliveryState.Unknown, Error = "发送结果未知：" + exception.GetType().Name };
                }
            }
        }
        return await RecordAsync(stored.Id, result).ConfigureAwait(false);
    }

    /// <summary>停止接收新发送并等待实际传输及记录退出，覆盖不属于插件调用的宿主发送。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _closing = true;
            if (_active == 0) _drained.TrySetResult();
        }
        await _stop.CancelAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所有发送实际退出后释放宿主取消源。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            _stop.Dispose();
            _disposed = true;
        }
    }

    /// <summary>使用独立收尾令牌记录已经发生的结果，不让调用者取消丢弃回执。</summary>
    private async Task<SendResult> RecordAsync(long id, TransportResult result)
    {
        try
        {
            await _store.SetDeliveryAsync(id, result, CancellationToken.None).ConfigureAwait(false);
            if (result.State == DeliveryState.Confirmed) _signal.Pulse();
            return new SendResult { MessageId = id, State = result.State, Recorded = true, Error = result.Error };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "发送 {MessageId} 的结果记录失败，不重新发送", id);
            return new SendResult
            {
                MessageId = id,
                State = result.State,
                Recorded = false,
                Error = result.State == DeliveryState.Confirmed ? "已取得平台确认，但回执记录失败。" : "发送结果记录失败，不自动重发。"
            };
        }
    }
}
