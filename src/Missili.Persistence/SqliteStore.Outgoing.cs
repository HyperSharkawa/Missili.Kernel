using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>出站审计沿用单写入执行器，网络和内容变换均在事务之外。</summary>
public sealed partial class SqliteStore : IOutgoingStore
{
    /// <summary>状态命令只读取有限批次结果，逐条迟到确认仍经统一读取路径合并。</summary>
    public async Task<ImmutableArray<BatchSendResult>> GetTaskBatchesAsync(MessageAddress address, Guid taskId, int limit, CancellationToken cancellationToken = default)
    {
        address.Validate();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        string key = JsonSerializer.Serialize(address);
        Guid[] ids = await ReadAsync(database => database.OutgoingBatches.AsNoTracking().Where(batch => batch.ConversationKey == key && batch.TaskId == taskId)
            .OrderByDescending(batch => batch.CreatedAt).ThenByDescending(batch => batch.Id).Take(limit).Select(batch => batch.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        ImmutableArray<BatchSendResult>.Builder results = ImmutableArray.CreateBuilder<BatchSendResult>();
        foreach (Guid id in ids)
        {
            OutgoingBatchSnapshot? batch = await GetBatchAsync(address, id, cancellationToken).ConfigureAwait(false);
            if (batch is not null) results.Add(batch.Result);
        }
        return results.ToImmutable();
    }

    /// <summary>一次批次身份只能创建一次，重复提交不能产生第二次网络发送。</summary>
    public Task CreateBatchAsync(OutgoingBatch batch, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(batch);
        return WriteAsync(database =>
        {
            database.OutgoingBatches.Add(new OutgoingBatchEntity
            {
                Id = batch.Id,
                RunId = runId,
                ConversationKey = JsonSerializer.Serialize(batch.Address),
                TaskId = batch.Origin.TaskId,
                AgentId = batch.Origin.AgentId,
                ToolInvocationId = batch.Origin.ToolInvocationId,
                Purpose = batch.Purpose,
                OriginalJson = json,
                CreatedAt = now.ToUnixTimeMilliseconds(),
                State = BatchSendState.Processing
            });
            database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>处理前固定配置，仅保存共享值；处理器失效也不丢失本批的选择依据。</summary>
    public Task SetBatchProcessingAsync(Guid batchId, OutgoingProcessingSnapshot processing, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(processing);
        return WriteAsync(database =>
        {
            OutgoingBatchEntity batch = database.OutgoingBatches.Single(item => item.Id == batchId);
            if (batch.State != BatchSendState.Processing || batch.ProcessingJson is not null) throw new InvalidOperationException("批次处理依据已固定或结束。");
            batch.ProcessingJson = json;
            database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>在审批前固定最终内容，审批以后再写正文属于契约错误。</summary>
    public Task SetBatchContentAsync(Guid batchId, ImmutableArray<MessageDraft> messages, ImmutableArray<OutgoingProcessorDescriptor> processors, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(messages), applied = JsonSerializer.Serialize(processors);
        return WriteAsync(database =>
        {
            OutgoingBatchEntity batch = database.OutgoingBatches.Single(item => item.Id == batchId);
            if (batch.State != BatchSendState.Processing) throw new InvalidOperationException("批次内容已经冻结或结束。");
            batch.FinalJson = json; batch.ProcessorsJson = applied; batch.State = BatchSendState.Reviewing;
            database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>批准记录保存成功才进入可以创建平台发送意图的阶段。</summary>
    public Task SetBatchApprovalAsync(Guid batchId, ToolApprovalRequest? request, ToolApprovalResult approval, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(approval), evidence = JsonSerializer.Serialize(request);
        return WriteAsync(database =>
        {
            OutgoingBatchEntity batch = database.OutgoingBatches.Single(item => item.Id == batchId);
            if (batch.State != BatchSendState.Reviewing) throw new InvalidOperationException("批次不处于审批阶段。");
            batch.ApprovalJson = json; batch.ApprovalRequestJson = evidence;
            batch.State = approval.Permitted ? BatchSendState.Sending : approval.State == ToolApprovalState.Denied ? BatchSendState.Rejected : BatchSendState.Failed;
            database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>批次结局与逐条回执共同保留，不根据结果未知自动恢复发送。</summary>
    public Task FinishBatchAsync(BatchSendResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result);
        return WriteAsync(database =>
        {
            OutgoingBatchEntity batch = database.OutgoingBatches.Single(item => item.Id == result.BatchId);
            batch.ResultJson = json; batch.State = result.State; batch.FinishedAt = now.ToUnixTimeMilliseconds();
            database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>读取时合并单条迟到回执，剩余草稿仍保持未发送，不产生任何外部动作。</summary>
    public Task<OutgoingBatchSnapshot?> GetBatchAsync(MessageAddress address, Guid batchId, CancellationToken cancellationToken = default)
    {
        address.Validate();
        string conversation = JsonSerializer.Serialize(address);
        return ReadAsync(database =>
        {
            OutgoingBatchEntity? row = database.OutgoingBatches.AsNoTracking().SingleOrDefault(item => item.Id == batchId && item.ConversationKey == conversation);
            if (row is null) return null;
            OutgoingBatch original = JsonSerializer.Deserialize<OutgoingBatch>(row.OriginalJson)!;
            ImmutableArray<MessageDraft> final = JsonSerializer.Deserialize<ImmutableArray<MessageDraft>>(row.FinalJson);
            BatchSendResult result = row.ResultJson is null ? new BatchSendResult { BatchId = row.Id, State = row.State, TotalMessages = final.Length, Recorded = true }
                : JsonSerializer.Deserialize<BatchSendResult>(row.ResultJson)!;
            List<OutgoingBatchMessageEntity> links = database.OutgoingBatchMessages.AsNoTracking().Where(item => item.BatchId == batchId)
                .Include(item => item.Message).OrderBy(item => item.Position).ToList();
            ImmutableArray<SendResult>.Builder receipts = result.Messages.ToBuilder();
            foreach (OutgoingBatchMessageEntity link in links)
            {
                MessageEntity message = ResolveCanonical(database, link.Message);
                SendResult receipt = new() { MessageId = link.MessageId, State = message.Delivery, Recorded = true, Error = message.DeliveryError };
                if (link.Position < receipts.Count)
                {
                    // 已取得但未写入消息行的确认仍是真实证据，不能被旧 Pending 状态覆盖。
                    if (message.Delivery == DeliveryState.Confirmed || receipts[link.Position].State != DeliveryState.Confirmed)
                        receipts[link.Position] = receipt;
                }
                else if (link.Position == receipts.Count) receipts.Add(receipt);
                else throw new InvalidDataException("出站批次意图位置不连续。");
            }
            BatchSendState state = result.State;
            if (receipts.Count > 0 && receipts.All(item => item.State == DeliveryState.Confirmed))
                state = receipts.Count == final.Length ? BatchSendState.Completed : BatchSendState.PartiallySent;
            else if (receipts.Any(item => item.State is DeliveryState.Unknown or DeliveryState.Accepted or DeliveryState.Sending)) state = BatchSendState.Unconfirmed;
            result = result with { State = state, Messages = receipts.ToImmutable() };
            return new OutgoingBatchSnapshot
            {
                Original = original,
                FinalMessages = final,
                Processors = JsonSerializer.Deserialize<ImmutableArray<OutgoingProcessorDescriptor>>(row.ProcessorsJson),
                Processing = row.ProcessingJson is null ? null : JsonSerializer.Deserialize<OutgoingProcessingSnapshot>(row.ProcessingJson),
                ApprovalRequest = row.ApprovalRequestJson is null ? null : JsonSerializer.Deserialize<ToolApprovalRequest>(row.ApprovalRequestJson),
                Approval = row.ApprovalJson is null ? null : JsonSerializer.Deserialize<ToolApprovalResult>(row.ApprovalJson),
                Result = result,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt),
                FinishedAt = row.FinishedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(row.FinishedAt.Value) : null
            };
        }, cancellationToken);
    }
}
