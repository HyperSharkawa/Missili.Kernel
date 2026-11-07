using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>模型业务数据复用现有有界读写执行器，事务中不等待网络。</summary>
public sealed partial class SqliteStore
{
    /// <summary>一次短事务固定高水位并按整批推进边界；增大窗口不会找回旧消息。</summary>
    public Task<ConversationWindow> CaptureWindowAsync(MessageAddress address, int maximum, int discard, CancellationToken cancellationToken)
    {
        address.Validate();
        if (maximum < 2 || discard < 1 || discard >= maximum) throw new ArgumentException("上下文窗口参数无效。");
        string key = JsonSerializer.Serialize(address);
        return WriteAsync(database =>
        {
            using IDbContextTransaction transaction = database.Database.BeginTransaction();
            ConversationWindowEntity? window = database.ConversationWindows.Find(key);
            if (window is null) { window = new ConversationWindowEntity { ConversationKey = key }; database.ConversationWindows.Add(window); }
            long high = database.Messages.Select(item => (long?)item.Id).Max() ?? 0;
            IQueryable<MessageEntity> source = InConversation(database.Messages.AsNoTracking(), address).Where(item => item.CanonicalId == null &&
                item.Id > window.DiscardedThrough && item.Id <= high && (item.Direction == MessageDirection.Incoming || item.Delivery == DeliveryState.Confirmed) &&
                !database.MessageContextPositions.Any(position => position.MessageId == item.Id && position.Position <= window.DiscardedThrough));
            int count = source.Count();
            if (count >= maximum)
            {
                int removed = checked(((count - maximum) / discard + 1) * discard);
                window.DiscardedThrough = OrderContext(source, database).Skip(removed - 1)
                    .Select(item => database.MessageContextPositions.Where(position => position.MessageId == item.Id).Select(position => (long?)position.Position).FirstOrDefault() ?? item.Id).First();
                window.Revision++;
            }
            List<MessageEntity> rows = OrderContext(source, database)
                .Include(item => item.Segments).ThenInclude(item => item.Media).ToList();
            database.SaveChanges(); transaction.Commit();
            return new ConversationWindow { HighWatermark = high, DiscardedThrough = window.DiscardedThrough, Revision = window.Revision, Messages = rows.Select(Snapshot).ToImmutableArray() };
        }, cancellationToken);
    }

    /// <summary>普通消息直接使用原 ID，发生别名合并时使用同事务保存的最早位置。</summary>
    private static IOrderedQueryable<MessageEntity> OrderContext(IQueryable<MessageEntity> source, MissiliDbContext database)
        => source.OrderBy(item => database.MessageContextPositions.Where(position => position.MessageId == item.Id).Select(position => (long?)position.Position).FirstOrDefault() ?? item.Id)
            .ThenBy(item => item.Id);

    /// <summary>必要调用记录保存成功后才允许协调器执行网络请求。</summary>
    public Task CreateCallAsync(ModelCallRecord call, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        database.ModelCalls.Add(new ModelCallEntity
        {
            Id = call.Id,
            RunId = call.RunId,
            Purpose = call.Purpose,
            OriginPluginId = call.Origin?.PluginId,
            TaskId = call.Origin?.TaskId,
            AgentId = call.Origin?.AgentId,
            TurnId = call.Origin?.TurnId,
            ToolInvocationId = call.Origin?.ToolInvocationId,
            OutgoingBatchId = call.Origin?.OutgoingBatchId,
            ProfileId = call.ProfileId,
            ConfigurationRevision = call.ConfigurationRevision,
            ConversationKey = call.Conversation is null ? null : JsonSerializer.Serialize(call.Conversation),
            TriggerMessageId = call.TriggerMessageId,
            StartedAt = call.StartedAt.ToUnixTimeMilliseconds(),
            State = ModelCallState.Running
        });
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>记录最终状态，结果未知或记录失败不触发业务补跑。</summary>
    public Task FinishCallAsync(Guid id, ModelResult result, DateTimeOffset now, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        ModelCallEntity row = database.ModelCalls.Single(item => item.Id == id);
        row.FinishedAt = now.ToUnixTimeMilliseconds(); row.State = result.State; row.ModelId = result.ModelId; row.Error = result.Error;
        row.ExcludedCandidatesJson = JsonSerializer.Serialize(result.ExcludedCandidates);
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>在相同尝试身份下更新结束结果，不能把一次重试覆盖成另一次。</summary>
    public Task SaveAttemptAsync(Guid callId, ModelAttemptSummary attempt, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        ModelAttemptEntity? row = database.ModelAttempts.Find(attempt.Id);
        if (row is null)
        { row = new ModelAttemptEntity { Id = attempt.Id, CallId = callId }; database.ModelAttempts.Add(row); }
        else if (row.CallId != callId) throw new ArgumentException("尝试不属于指定调用。");
        row.ModelId = attempt.ModelId; row.ProviderId = attempt.ProviderId; row.Generation = attempt.Generation;
        row.Round = attempt.Round; row.Sequence = attempt.Sequence; row.StartedAt = attempt.StartedAt.ToUnixTimeMilliseconds();
        row.FinishedAt = attempt.FinishedAt?.ToUnixTimeMilliseconds(); row.State = attempt.State; row.Error = attempt.Error;
        row.ErrorKind = attempt.ErrorKind; row.HttpStatus = attempt.HttpStatus; row.RequestId = attempt.RequestId;
        row.InputTokens = attempt.Usage.InputTokens; row.OutputTokens = attempt.Usage.OutputTokens;
        row.CachedInputTokens = attempt.Usage.CachedInputTokens; row.CacheWriteTokens = attempt.Usage.CacheWriteTokens;
        row.HasPartialResult = attempt.HasPartialResult; row.DetailId = attempt.DetailId;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>读取完整尝试列表，返回值脱离 EF 跟踪。</summary>
    public Task<ModelCallRecord?> GetCallAsync(Guid id, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        ModelCallEntity? row = database.ModelCalls.AsNoTracking().Include(item => item.Attempts).SingleOrDefault(item => item.Id == id);
        return row is null ? null : CallSnapshot(row);
    }, cancellationToken);

    /// <summary>明确字段筛选和序号游标全部在 SQLite 执行。</summary>
    public Task<ModelCallPage> QueryCallsAsync(ModelCallQuery query, CancellationToken cancellationToken)
    {
        if (query.PageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.StartedFrom > query.StartedUntil) throw new ArgumentException("调用时间范围无效。", nameof(query));
        long? from = query.StartedFrom?.ToUnixTimeMilliseconds(), until = query.StartedUntil?.ToUnixTimeMilliseconds();
        string? key = query.Conversation is null ? null : JsonSerializer.Serialize(query.Conversation);
        return ReadAsync(database =>
        {
            IQueryable<ModelCallEntity> source = database.ModelCalls.AsNoTracking();
            if (key is not null) source = source.Where(item => item.ConversationKey == key);
            if (query.Purpose is not null) source = source.Where(item => item.Purpose == query.Purpose);
            if (query.State.HasValue) source = source.Where(item => item.State == query.State.Value);
            if (from.HasValue) source = source.Where(item => item.StartedAt >= from.Value);
            if (until.HasValue) source = source.Where(item => item.StartedAt < until.Value);
            if (query.BeforeSequence.HasValue) source = source.Where(item => item.Sequence < query.BeforeSequence.Value);
            List<ModelCallEntity> rows = source.OrderByDescending(item => item.Sequence).Include(item => item.Attempts).Take(query.PageSize + 1).ToList();
            bool more = rows.Count > query.PageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            return new ModelCallPage { Calls = rows.Select(CallSnapshot).ToImmutableArray(), NextSequence = more ? rows[^1].Sequence : null };
        }, cancellationToken);
    }

    /// <summary>详情只能通过已保存的尝试标识定位。</summary>
    public Task<string?> GetAttemptDetailAsync(Guid attemptId, CancellationToken cancellationToken) => ReadAsync(database =>
        database.ModelAttempts.Where(item => item.Id == attemptId).Select(item => item.DetailId).SingleOrDefault(), cancellationToken);

    /// <summary>只有仍可用的派生原件才可复用。</summary>
    public Task<Guid?> GetRepresentationAsync(string contentKey, string kind, CancellationToken cancellationToken) => ReadAsync(database =>
        database.MediaRepresentations.Where(item => item.ContentKey == contentKey && item.Kind == kind &&
            database.MediaReferences.Any(media => media.Id == item.MediaId && media.State == MediaState.Available)).Select(item => (Guid?)item.MediaId).SingleOrDefault(), cancellationToken);

    /// <summary>单写入执行器内按内容和表示去重，输出保持独立媒体引用。</summary>
    public Task<Guid> SaveRepresentationAsync(string contentKey, string kind, Guid mediaId, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        MediaRepresentationEntity? existing = database.MediaRepresentations.Find(contentKey, kind);
        if (existing is not null) return existing.MediaId;
        if (!database.MediaReferences.Any(item => item.Id == mediaId && item.State == MediaState.Available)) throw new ArgumentException("派生原件尚不可用。");
        database.MediaRepresentations.Add(new MediaRepresentationEntity { ContentKey = contentKey, Kind = kind, MediaId = mediaId });
        database.SaveChanges(); return mediaId;
    }, cancellationToken);

    /// <summary>以消息为分页单位，一条消息内的所有媒体一起返回，避免同序号翻页遗漏。</summary>
    public Task<TranscriptionMediaPage> ScanTranscriptionMediaAsync(long after, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(count));
        return ReadAsync(database =>
        {
            List<MessageEntity> messages = database.Messages.AsNoTracking().Where(item => item.Id > after && item.CanonicalId == null &&
                item.Segments.Any(segment => segment.Media.Count > 0 || segment.ContentId != null)).OrderBy(item => item.Id).Take(count)
                .Include(item => item.Segments).ThenInclude(item => item.Media).ToList();
            ImmutableArray<TranscriptionMedia>.Builder result = ImmutableArray.CreateBuilder<TranscriptionMedia>();
            foreach (MessageEntity message in messages)
            {
                Guid[] contents = message.Segments.Where(item => item.ContentId.HasValue).Select(item => item.ContentId!.Value).ToArray();
                Guid[] mediaIds = message.Segments.SelectMany(item => item.Media.Select(link => link.MediaId)).Concat(
                    database.MediaBindings.Where(item => item.ContentId != null && contents.Contains(item.ContentId.Value)).Select(item => item.MediaId)).Distinct().ToArray();
                List<MediaEntity> media = database.MediaReferences.AsNoTracking().Where(item => mediaIds.Contains(item.Id)).ToList();
                foreach (MediaEntity item in media)
                    result.Add(new TranscriptionMedia { MediaId = item.Id, Address = Address(message), Kind = item.Kind, Sequence = message.Id });
            }
            return new TranscriptionMediaPage { Items = result.ToImmutable(), NextMessageId = messages.Count == count ? messages[^1].Id : null };
        }, cancellationToken);
    }

    /// <summary>时间恢复为带偏移类型，数据库排序字段仍使用 UTC 毫秒。</summary>
    private static ModelCallRecord CallSnapshot(ModelCallEntity row) => new()
    {
        Id = row.Id,
        RunId = row.RunId,
        Purpose = row.Purpose,
        Origin = row.TaskId is null && row.AgentId is null && row.TurnId is null && row.ToolInvocationId is null && row.OriginPluginId is null && row.OutgoingBatchId is null ? null : new OperationOrigin
        { PluginId = row.OriginPluginId, TaskId = row.TaskId, AgentId = row.AgentId, TurnId = row.TurnId, ToolInvocationId = row.ToolInvocationId, OutgoingBatchId = row.OutgoingBatchId },
        ProfileId = row.ProfileId,
        ConfigurationRevision = row.ConfigurationRevision,
        Conversation = row.ConversationKey is null ? null : JsonSerializer.Deserialize<MessageAddress>(row.ConversationKey),
        TriggerMessageId = row.TriggerMessageId,
        StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt),
        FinishedAt = row.FinishedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(row.FinishedAt.Value) : null,
        State = row.State,
        ModelId = row.ModelId,
        Error = row.Error,
        ExcludedCandidates = JsonSerializer.Deserialize<ImmutableArray<ModelCandidateExclusion>>(row.ExcludedCandidatesJson),
        Attempts = row.Attempts.OrderBy(item => item.Sequence).Select(AttemptSnapshot).ToImmutableArray()
    };

    /// <summary>复制尝试状态和未知用量，不返回私有异常或数据库实体。</summary>
    private static ModelAttemptSummary AttemptSnapshot(ModelAttemptEntity row) => new()
    {
        Id = row.Id,
        ModelId = row.ModelId,
        ProviderId = row.ProviderId,
        Generation = row.Generation,
        Round = row.Round,
        Sequence = row.Sequence,
        StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt),
        FinishedAt = row.FinishedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(row.FinishedAt.Value) : null,
        State = row.State,
        Error = row.Error,
        ErrorKind = row.ErrorKind,
        HttpStatus = row.HttpStatus,
        RequestId = row.RequestId,
        HasPartialResult = row.HasPartialResult,
        DetailId = row.DetailId,
        Usage = new ModelUsage { InputTokens = row.InputTokens, OutputTokens = row.OutputTokens, CachedInputTokens = row.CachedInputTokens, CacheWriteTokens = row.CacheWriteTokens }
    };
}
