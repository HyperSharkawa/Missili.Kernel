using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>保存发送意图后至多调用一次传输；本类没有任何具体平台实现。</summary>
public sealed class MessageSender : IMessageSender, ISendReceiptSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    private bool _disposed;
    private readonly IInfrastructureStore _store;
    private readonly IOutgoingStore _batches;
    private readonly OutgoingPipeline _pipeline;
    private readonly IReadOnlyDictionary<string, IMessageTransport> _transports;
    private readonly IAdapterRuntime _adapters;
    private readonly DispatchSignal _signal;
    private readonly Guid _runId;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>组装明确注册的传输实现；空集合表示尚未接入平台。</summary>
    public MessageSender(IInfrastructureStore store, IOutgoingStore batches, OutgoingPipeline pipeline, IEnumerable<IMessageTransport> transports, DispatchSignal signal, Guid runId, ILogger<MessageSender> logger, TimeProvider clock, IAdapterRuntime adapters)
    {
        _store = store;
        _batches = batches;
        _pipeline = pipeline;
        _transports = transports.ToDictionary(item => item.AdapterId, StringComparer.Ordinal);
        _adapters = adapters;
        _signal = signal;
        _runId = runId;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>意图或发送中状态写入失败时不调用传输；未知结果不自动重发。</summary>
    public async Task<BatchSendResult> SendAsync(OutgoingBatch batch, CancellationToken cancellationToken = default)
    {
        if (OutgoingPipeline.IsProcessing) throw new InvalidOperationException("出站处理器必须返回内容，不能重入消息发送服务。");
        batch.Address.Validate();
        if (batch.Id == Guid.Empty || batch.Messages.IsDefaultOrEmpty || string.IsNullOrWhiteSpace(batch.Purpose)) throw new ArgumentException("发送批次身份或内容无效。");
        foreach (MessageDraft draft in batch.Messages) ContentValidation.Validate(draft.Segments);
        batch = batch.Freeze() with { Origin = batch.Origin with { OutgoingBatchId = batch.Id } };
        lock (_gate)
        {
            if (_closing) return new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Cancelled, Recorded = false, Error = "宿主已停止接收发送。" };
            _active++;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { return await SendBatchAsync(batch, linked.Token).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _active--;
                if (_closing && _active == 0) _drained.TrySetResult();
            }
        }
    }

    /// <summary>一个批次只运行一次处理链，逐条确认后才开始下一条消息。</summary>
    private async Task<BatchSendResult> SendBatchAsync(OutgoingBatch batch, CancellationToken token)
    {
        ImmutableArray<SendResult>.Builder receipts = ImmutableArray.CreateBuilder<SendResult>();
        int count = batch.Messages.Length;
        bool created = false;
        BatchSendResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            await _batches.CreateBatchAsync(batch, _runId, _clock.GetUtcNow(), token).ConfigureAwait(false);
            created = true;
            PreparedOutgoing prepared = await _pipeline.PrepareAsync(batch, token).ConfigureAwait(false);
            if (prepared.Failure is not null) result = prepared.Failure;
            else
            {
                count = prepared.Messages.Length;
                result = new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Completed, TotalMessages = count, Recorded = true };
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    BatchSendState? eligibility = await _pipeline.CanCommitAsync(batch, prepared, token).ConfigureAwait(false);
                    if (eligibility.HasValue)
                    { result = result with { State = eligibility.Value, ErrorKind = "EligibilityChanged", Error = "任务、权限或相关审批政策发生变化，剩余消息未发送。" }; break; }
                    MessageDraft draft = prepared.Messages[index];
                    SendResult receipt = await SendCoreAsync(new OutgoingMessage
                    { Address = batch.Address, Segments = draft.Segments, Metadata = draft.Metadata, BatchId = batch.Id, BatchPosition = index }, token).ConfigureAwait(false);
                    receipts.Add(receipt);
                    if (!receipt.Recorded || receipt.State != DeliveryState.Confirmed)
                    {
                        BatchSendState state = receipt.State is DeliveryState.Unknown or DeliveryState.Accepted ? BatchSendState.Unconfirmed
                            : receipt.State == DeliveryState.Cancelled ? BatchSendState.Cancelled
                            : receipts.Any(item => item.State == DeliveryState.Confirmed) ? BatchSendState.PartiallySent : BatchSendState.Failed;
                        if (receipt.State == DeliveryState.Confirmed && receipts.Count == count) state = BatchSendState.Completed;
                        result = result with { State = state, Recorded = receipt.Recorded, Error = receipt.Error, ErrorKind = receipt.Recorded ? "Delivery" : "Storage" };
                        break;
                    }
                }
                result = result with { Messages = receipts.ToImmutable() };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result = new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Cancelled, TotalMessages = count, Messages = receipts.ToImmutable(), Recorded = created, ErrorKind = "Cancelled" }; }
        catch (Exception exception)
        {
            _logger.LogError("出站批次 {BatchId} 基础设施失败：{ErrorType}", batch.Id, exception.GetType().Name);
            result = new BatchSendResult
            {
                BatchId = batch.Id,
                State = BatchSendState.Failed,
                TotalMessages = count,
                Messages = receipts.ToImmutable(),
                Recorded = false,
                Error = "出站批次未完成：" + exception.GetType().Name,
                ErrorKind = "Infrastructure"
            };
        }
        if (!created) return result;
        try { await _batches.FinishBatchAsync(result, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogError("批次 {BatchId} 结果记录失败：{ErrorType}", batch.Id, exception.GetType().Name);
            result = result with { Recorded = false, ErrorKind = "Storage", Error = "批次结果记录失败；已有单条平台结果仍保留，不重新发送。" };
        }
        return result;
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
        _transports.TryGetValue(message.Address.AdapterId, out IMessageTransport? transport);
        if (cancellationToken.IsCancellationRequested)
            result = new TransportResult { State = DeliveryState.Cancelled };
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
                    result = transport is not null ? await transport.SendAsync(stored.Id, message, cancellationToken).ConfigureAwait(false)
                        : await _adapters.SendAsync(stored.Id, message, cancellationToken).ConfigureAwait(false);
                    if (result.State is not (DeliveryState.Confirmed or DeliveryState.Failed or DeliveryState.Unknown or DeliveryState.Cancelled or DeliveryState.Accepted))
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

    /// <summary>迟到确认仅更新已保存发送状态；发送后通知由存储首次确认状态控制。</summary>
    public async Task<SendResult> RecordReceiptAsync(long messageId, TransportResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.State is not (DeliveryState.Confirmed or DeliveryState.Failed or DeliveryState.Accepted)) throw new ArgumentException("迟到回执必须包含明确平台结果。");
        lock (_gate)
        {
            if (_closing) return new SendResult { MessageId = messageId, State = result.State, Recorded = false, Error = "宿主已停止记录新回执。" };
            _active++;
        }
        try { return await RecordAsync(messageId, result).ConfigureAwait(false); }
        finally { lock (_gate) { _active--; if (_closing && _active == 0) _drained.TrySetResult(); } }
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
