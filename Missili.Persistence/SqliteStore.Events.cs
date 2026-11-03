using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>业务事件保存、撤回关联及独立历史查询。</summary>
public sealed partial class SqliteStore
{
    /// <summary>在一个事务中保存事件与撤回事实，事件没有可靠标识时不猜测去重。</summary>
    public Task<EventReceipt> AppendEventAsync(IncomingPlatformEvent input, DateTimeOffset receivedAt, Guid runId, CancellationToken cancellationToken)
    {
        input.Validate();
        input = input.Freeze();
        return WriteAsync(database =>
        {
            using IDbContextTransaction transaction = database.Database.BeginTransaction();
            if (input.DeduplicationKey is not null)
            {
                PlatformEventEntity? duplicate = database.PlatformEvents.SingleOrDefault(item => item.AdapterId == input.AdapterId &&
                    item.BotAccountId == input.BotAccountId && item.DeduplicationKey == input.DeduplicationKey);
                if (duplicate is not null) return new EventReceipt { Event = EventSnapshot(duplicate), Duplicate = true };
            }
            PlatformEventEntity row = new()
            {
                AdapterId = input.AdapterId,
                PlatformId = input.PlatformId,
                BotAccountId = input.BotAccountId,
                Type = input.Type,
                SubType = input.SubType,
                Category = input.Category,
                ConversationKind = input.ConversationKind,
                ConversationId = input.ConversationId,
                ActorId = input.ActorId,
                TargetUserId = input.TargetUserId,
                TargetMessageId = input.TargetMessageId,
                DeduplicationKey = input.DeduplicationKey,
                RecallsMessage = input.RecallsMessage,
                ReceivedAt = receivedAt.ToUnixTimeMilliseconds(),
                OccurredAt = input.OccurredAt?.ToUnixTimeMilliseconds(),
                PayloadJson = input.Payload?.GetRawText(),
                RunId = runId
            };
            database.PlatformEvents.Add(row);
            if (input.RecallsMessage)
            {
                database.Messages.Where(item => item.AdapterId == input.AdapterId && item.BotAccountId == input.BotAccountId &&
                    item.PlatformId == input.PlatformId && item.ConversationKind == input.ConversationKind &&
                    item.ConversationId == input.ConversationId && item.PlatformMessageId == input.TargetMessageId)
                    .ExecuteUpdate(setters => setters.SetProperty(item => item.Recalled, true));
            }
            database.SaveChanges();
            transaction.Commit();
            return new EventReceipt { Event = EventSnapshot(row) };
        }, cancellationToken);
    }

    /// <summary>只领取本次运行的有限事件，不重放其他运行遗留业务。</summary>
    public Task<ImmutableArray<PlatformEventSnapshot>> ClaimEventsAsync(Guid runId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return WriteAsync(database =>
        {
            List<PlatformEventEntity> rows = database.PlatformEvents.Where(item => item.RunId == runId && item.Processing == ProcessingState.Pending)
                .OrderBy(item => item.Id).Take(count).ToList();
            foreach (PlatformEventEntity row in rows) row.Processing = ProcessingState.Running;
            database.SaveChanges();
            return rows.Select(EventSnapshot).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>记录实际结束状态，不对插件失败自动重试。</summary>
    public Task SetEventProcessingAsync(long id, ProcessingState state, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        if (database.PlatformEvents.Where(item => item.Id == id).ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, state)) != 1)
            throw new KeyNotFoundException("事件不存在。");
        return true;
    }, cancellationToken);

    /// <summary>读取已保存事件，返回脱离实体跟踪的快照。</summary>
    public Task<PlatformEventSnapshot?> GetEventAsync(long id, CancellationToken cancellationToken = default) => ReadAsync(database =>
    {
        PlatformEventEntity? row = database.PlatformEvents.AsNoTracking().SingleOrDefault(item => item.Id == id);
        return row is null ? null : EventSnapshot(row);
    }, cancellationToken);

    /// <summary>在数据库端执行明确过滤与稳定游标，不加载全部事件后筛选。</summary>
    public Task<EventPage> QueryEventsAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        if (query.PageSize is < 1 or > 500 || !Enum.IsDefined(query.PageDirection)) throw new ArgumentException("事件分页参数无效。");
        if (query.Category.HasValue && !Enum.IsDefined(query.Category.Value)) throw new ArgumentException("事件类别无效。");
        if (query.From.HasValue && query.Until.HasValue && query.From >= query.Until) throw new ArgumentException("事件时间范围无效。");
        return ReadAsync(database =>
        {
            IQueryable<PlatformEventEntity> rows = database.PlatformEvents.AsNoTracking();
            if (query.AdapterId is not null) rows = rows.Where(item => item.AdapterId == query.AdapterId);
            if (query.BotAccountId is not null) rows = rows.Where(item => item.BotAccountId == query.BotAccountId);
            if (query.Type is not null) rows = rows.Where(item => item.Type == query.Type);
            if (query.ActorId is not null) rows = rows.Where(item => item.ActorId == query.ActorId);
            if (query.ConversationKind is not null) rows = rows.Where(item => item.ConversationKind == query.ConversationKind);
            if (query.ConversationId is not null) rows = rows.Where(item => item.ConversationId == query.ConversationId);
            if (query.Category.HasValue) rows = rows.Where(item => item.Category == query.Category);
            long? from = query.From?.ToUnixTimeMilliseconds(), until = query.Until?.ToUnixTimeMilliseconds();
            if (from.HasValue) rows = rows.Where(item => item.ReceivedAt >= from.Value);
            if (until.HasValue) rows = rows.Where(item => item.ReceivedAt < until.Value);
            bool forward = query.PageDirection == PageDirection.Forward;
            if (query.Cursor is not null)
            {
                long time = query.Cursor.UnixMilliseconds, id = query.Cursor.MessageId;
                rows = forward ? rows.Where(item => item.ReceivedAt > time || (item.ReceivedAt == time && item.Id > id))
                    : rows.Where(item => item.ReceivedAt < time || (item.ReceivedAt == time && item.Id < id));
            }
            rows = forward ? rows.OrderBy(item => item.ReceivedAt).ThenBy(item => item.Id) : rows.OrderByDescending(item => item.ReceivedAt).ThenByDescending(item => item.Id);
            List<PlatformEventEntity> page = rows.Take(query.PageSize + 1).ToList();
            bool more = page.Count > query.PageSize;
            if (more) page.RemoveAt(page.Count - 1);
            PlatformEventEntity? last = page.LastOrDefault();
            return new EventPage
            {
                Events = page.Select(EventSnapshot).ToImmutableArray(),
                NextCursor = more && last is not null ? new HistoryCursor { MessageId = last.Id, UnixMilliseconds = last.ReceivedAt } : null
            };
        }, cancellationToken);
    }

    /// <summary>复制平台事件的全部有效字段，不把通知转成消息段。</summary>
    private static PlatformEventSnapshot EventSnapshot(PlatformEventEntity row) => new()
    {
        Id = row.Id,
        ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.ReceivedAt),
        Processing = row.Processing,
        Event = new IncomingPlatformEvent
        {
            AdapterId = row.AdapterId,
            PlatformId = row.PlatformId,
            BotAccountId = row.BotAccountId,
            Type = row.Type,
            SubType = row.SubType,
            Category = row.Category,
            ConversationKind = row.ConversationKind,
            ConversationId = row.ConversationId,
            ActorId = row.ActorId,
            TargetUserId = row.TargetUserId,
            TargetMessageId = row.TargetMessageId,
            DeduplicationKey = row.DeduplicationKey,
            RecallsMessage = row.RecallsMessage,
            OccurredAt = row.OccurredAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(row.OccurredAt.Value) : null,
            Payload = ParseJson(row.PayloadJson)
        }
    };
}
