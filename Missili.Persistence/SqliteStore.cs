using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>真实 SQLite 存储，写入串行化、读取有界且每项拥有独立上下文。</summary>
public sealed partial class SqliteStore : IInfrastructureStore
{
    private readonly DbContextOptions<MissiliDbContext> _options;
    private readonly DatabaseExecutor _writes;
    private readonly DatabaseExecutor _reads;

    /// <summary>配置数据文件及工作容量；构造不执行迁移。</summary>
    public SqliteStore(string databasePath, InfrastructureOptions options)
    {
        options.Validate();
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        SqliteConnectionStringBuilder connection = new() { DataSource = fullPath, ForeignKeys = true };
        _options = new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(connection.ToString()).Options;
        _writes = new DatabaseExecutor(options.WriteCapacity, 1);
        _reads = new DatabaseExecutor(options.WriteCapacity, options.ReadConcurrency);
    }

    /// <summary>执行迁移并恢复状态；只恢复媒体，不重放业务。</summary>
    public Task InitializeAsync(Guid runId, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        database.Database.Migrate();
        database.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        database.Messages.Where(item => item.RunId != runId &&
            (item.Processing == ProcessingState.Pending || item.Processing == ProcessingState.Running))
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, ProcessingState.Interrupted));
        database.Messages.Where(item => item.RunId != runId && item.Delivery == DeliveryState.Sending)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Delivery, DeliveryState.Unknown));
        database.Messages.Where(item => item.RunId != runId && item.Delivery == DeliveryState.Pending)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Delivery, DeliveryState.Cancelled));
        database.Database.ExecuteSqlRaw("UPDATE MediaReferences SET State = 4, Error = '流导入被进程中断，原始输入不可恢复。' WHERE State = 1 AND json_extract(SourceJson, '$.Kind') = 'import';");
        database.MediaReferences.Where(item => item.State == MediaState.Downloading)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.State, MediaState.Pending));
        database.PlatformEvents.Where(item => item.RunId != runId && (item.Processing == ProcessingState.Pending || item.Processing == ProcessingState.Running))
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, ProcessingState.Interrupted));
        database.StructuredContents.Where(item => item.State == ContentState.Expanding || item.State == ContentState.WaitingForAdapter)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.State, ContentState.Pending).SetProperty(item => item.Claimed, false));
        database.MediaReferences.Where(item => item.State == MediaState.WaitingForAdapter)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.State, MediaState.Pending));
        database.MediaReferences.Where(item => item.Claimed)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Claimed, false));
        return true;
    }, cancellationToken);

    /// <summary>执行实际只读查询，不返回连接字符串或本地路径。</summary>
    public Task CheckHealthAsync(CancellationToken cancellationToken) => ReadAsync(database =>
    {
        _ = database.Messages.AsNoTracking().Select(item => item.Id).Take(1).ToArray();
        return true;
    }, cancellationToken);

    /// <summary>原子追加消息和所有媒体引用，只有消息去重冲突被视为重复。</summary>
    public Task<ReceiveReceipt> AppendAsync(IncomingMessage message, DateTimeOffset receivedAt, Guid runId, CancellationToken cancellationToken)
    {
        message.Validate();
        message = message.Freeze();
        return WriteAsync(database =>
        {
            MessageEntity entity = CreateMessage(message.Address, message.SenderId, receivedAt, runId);
            entity.Direction = message.Direction;
            entity.Delivery = message.Direction == MessageDirection.Outgoing ? DeliveryState.Confirmed : DeliveryState.NotApplicable;
            entity.PlatformMessageId = message.PlatformMessageId;
            entity.DeduplicationKey = message.DeduplicationKey;
            entity.OccurredAt = message.OccurredAt?.ToUnixTimeMilliseconds();
            entity.MetadataJson = message.Metadata?.GetRawText();
            entity.Recalled = HasRecall(database, entity);
            if (message.PlatformMessageId is not null)
            {
                MessageEntity? observed = WithSegments(InConversation(database.Messages, message.Address)).SingleOrDefault(item =>
                    item.CanonicalId == null && item.Direction == message.Direction && item.PlatformMessageId == message.PlatformMessageId);
                if (observed is not null) return new ReceiveReceipt { Message = Snapshot(observed), Duplicate = true };
            }
            for (int index = 0; index < message.Segments.Length; index++)
            {
                entity.Segments.Add(CreateSegment(database, message.Segments[index], message.Address, index, receivedAt));
            }
            database.Messages.Add(entity);
            try
            {
                database.SaveChanges();
                return new ReceiveReceipt { Message = Snapshot(entity) };
            }
            catch (DbUpdateException exception) when (message.DeduplicationKey is not null &&
                exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } conflict &&
                conflict.Message.Contains("Messages.DeduplicationKey", StringComparison.Ordinal))
            {
                // 只有明确发生于消息去重索引的冲突才归类为重复。
                database.ChangeTracker.Clear();
                MessageEntity? existing = database.Messages.AsNoTracking().Include(item => item.Segments).ThenInclude(item => item.Media)
                    .SingleOrDefault(item => item.AdapterId == message.Address.AdapterId &&
                        item.BotAccountId == message.Address.BotAccountId && item.DeduplicationKey == message.DeduplicationKey);
                if (existing is null) throw;
                return new ReceiveReceipt { Message = Snapshot(existing), Duplicate = true };
            }
        }, cancellationToken);
    }

    /// <summary>保存发送意图，媒体段复制引用而不复制共享原件。</summary>
    public Task<MessageSnapshot> AppendOutgoingAsync(OutgoingMessage message, DateTimeOffset now, Guid runId, CancellationToken cancellationToken)
    {
        message.Address.Validate();
        ContentValidation.Validate(message.Segments);
        message = message.Freeze();
        return WriteAsync(database =>
        {
            MessageEntity entity = CreateMessage(message.Address, message.Address.BotAccountId, now, runId);
            entity.Direction = MessageDirection.Outgoing;
            entity.Delivery = DeliveryState.Pending;
            entity.Processing = ProcessingState.Finished;
            entity.MetadataJson = message.Metadata?.GetRawText();
            for (int index = 0; index < message.Segments.Length; index++)
            {
                MessageSegment segment = message.Segments[index];
                SegmentEntity row = new()
                {
                    Position = index,
                    Kind = segment.Kind,
                    Version = segment.Version,
                    Text = segment.Text,
                    PayloadJson = segment.Payload?.GetRawText(),
                    ParseState = segment.ParseState,
                    ParseError = segment.ParseError,
                    ContentId = segment.ContentId
                };
                // 已清理或不存在的引用不能伪造为可以发送的文件。
                for (int ordinal = 0; ordinal < segment.MediaIds.Length; ordinal++)
                {
                    MediaEntity original = database.MediaReferences.AsNoTracking().Single(item => item.Id == segment.MediaIds[ordinal]);
                    if (original.State != MediaState.Available) throw new InvalidOperationException("发送媒体原件尚不可用。");
                    Guid mediaId = Guid.NewGuid();
                    database.MediaReferences.Add(new MediaEntity
                    {
                        Id = mediaId,
                        Kind = original.Kind,
                        SourceJson = original.SourceJson,
                        AdapterId = original.AdapterId,
                        RepresentationJson = original.RepresentationJson,
                        SourceKey = original.SourceKey,
                        ContentKey = original.ContentKey,
                        Length = original.Length,
                        RelativePath = original.RelativePath,
                        State = MediaState.Available,
                        CreatedAt = now.ToUnixTimeMilliseconds()
                    });
                    row.Media.Add(new MediaBindingEntity { MediaId = mediaId, Ordinal = ordinal });
                }
                entity.Segments.Add(row);
            }
            database.Messages.Add(entity);
            database.SaveChanges();
            return Snapshot(entity);
        }, cancellationToken);
    }

    /// <summary>保存传输状态；只有确认成功才产生发送后通知。</summary>
    public Task SetDeliveryAsync(long id, TransportResult result, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        MessageEntity message = ResolveCanonical(database, database.Messages.Single(item => item.Id == id));
        if (message.Direction != MessageDirection.Outgoing) throw new InvalidOperationException("只有发送意图可以更新回执。");
        // 平台自身事件可能先于回执到达，已经确认的事实不能被迟到超时降级。
        if (message.Delivery == DeliveryState.Confirmed && result.State != DeliveryState.Confirmed) return true;
        if (result.State == DeliveryState.Confirmed && result.PlatformMessageId is not null)
        {
            MessageEntity? existing = InConversation(database.Messages, Address(message)).SingleOrDefault(item =>
                item.Id != message.Id && item.CanonicalId == null && item.Direction == MessageDirection.Outgoing && item.PlatformMessageId == result.PlatformMessageId);
            if (existing is not null)
            {
                message.CanonicalId = existing.Id;
                message.Processing = ProcessingState.Finished;
                message.Delivery = DeliveryState.Confirmed;
                message.PlatformMessageId = result.PlatformMessageId;
                existing.Recalled |= message.Recalled;
                database.SaveChanges();
                return true;
            }
        }
        bool newlyConfirmed = message.Delivery != DeliveryState.Confirmed && result.State == DeliveryState.Confirmed;
        message.Delivery = result.State;
        message.PlatformMessageId = result.PlatformMessageId ?? message.PlatformMessageId;
        message.DeliveryError = result.Error;
        message.Recalled |= HasRecall(database, message);
        if (newlyConfirmed) message.Processing = ProcessingState.Pending;
        database.SaveChanges();
        return true;
    }, cancellationToken);

    /// <summary>有限领取本次运行的待分发记录，不扫描或重放上次业务。</summary>
    public Task<ImmutableArray<MessageSnapshot>> ClaimMessagesAsync(Guid runId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return WriteAsync(database =>
        {
            List<MessageEntity> messages = database.Messages.Include(item => item.Segments).ThenInclude(item => item.Media)
                .Where(item => item.CanonicalId == null && item.RunId == runId && item.Processing == ProcessingState.Pending)
                .OrderBy(item => item.Id).Take(count).ToList();
            foreach (MessageEntity message in messages) message.Processing = ProcessingState.Running;
            database.SaveChanges();
            return messages.Select(Snapshot).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>记录业务最终状态，不自动重新入队。</summary>
    public Task SetProcessingAsync(long id, ProcessingState state, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        int changed = database.Messages.Where(item => item.Id == id).ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, state));
        if (changed != 1) throw new KeyNotFoundException("消息不存在。");
        return true;
    }, cancellationToken);

    /// <summary>独立保存撤回与删除标记，不修改任何消息段。</summary>
    public Task SetVisibilityAsync(long id, bool recalled, bool deleted, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        int changed = database.Messages.Where(item => item.Id == id).ExecuteUpdate(setters =>
            setters.SetProperty(item => item.Recalled, recalled).SetProperty(item => item.Deleted, deleted));
        if (changed != 1) throw new KeyNotFoundException("消息不存在。");
        return true;
    }, cancellationToken);

    /// <summary>只标记当前运行的未完成状态，不触发重新分发或补发。</summary>
    public Task InterruptRunAsync(Guid runId, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        database.PlatformEvents.Where(item => item.RunId == runId && (item.Processing == ProcessingState.Pending || item.Processing == ProcessingState.Running))
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, ProcessingState.Interrupted));
        database.Messages.Where(item => item.RunId == runId &&
            (item.Processing == ProcessingState.Pending || item.Processing == ProcessingState.Running))
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Processing, ProcessingState.Interrupted));
        database.Messages.Where(item => item.RunId == runId && item.Delivery == DeliveryState.Sending)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Delivery, DeliveryState.Unknown));
        database.Messages.Where(item => item.RunId == runId && item.Delivery == DeliveryState.Pending)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Delivery, DeliveryState.Cancelled));
        return true;
    }, cancellationToken);

    /// <summary>按主键读取包含有序消息段的脱离跟踪快照。</summary>
    public Task<MessageSnapshot?> GetAsync(long id, CancellationToken cancellationToken = default) => ReadAsync(database =>
    {
        MessageEntity? entity = database.Messages.AsNoTracking().Include(item => item.Segments).ThenInclude(item => item.Media).SingleOrDefault(item => item.Id == id);
        if (entity is null) return null;
        MessageEntity canonical = ResolveCanonical(database, entity);
        if (canonical.Id != entity.Id) canonical = WithSegments(database.Messages.AsNoTracking()).Single(item => item.Id == canonical.Id);
        return Snapshot(canonical);
    }, cancellationToken);

    /// <summary>执行数据库端筛选及游标分页；返回顺序与翻页方向相同。</summary>
    public Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.PageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query), "页大小必须在 1 至 500 之间。");
        if (!Enum.IsDefined(query.Time) || !Enum.IsDefined(query.PageDirection)) throw new ArgumentException("历史查询排序参数无效。");
        if (query.From.HasValue && query.Until.HasValue && query.From >= query.Until) throw new ArgumentException("时间区间必须递增。");
        query.Address?.Validate();
        return ReadAsync(database =>
        {
            IQueryable<MessageEntity> source = database.Messages.AsNoTracking().Where(item => item.CanonicalId == null);
            if (query.Address is not null) source = InConversation(source, query.Address);
            if (query.SenderId is not null) source = source.Where(item => item.SenderId == query.SenderId);
            if (query.Direction.HasValue) source = source.Where(item => item.Direction == query.Direction.Value);
            if (query.SegmentType is not null) source = source.Where(item => item.Segments.Any(segment => segment.Kind == query.SegmentType));
            if (!query.IncludeRecalled) source = source.Where(item => !item.Recalled);
            if (!query.IncludeDeleted) source = source.Where(item => !item.Deleted);
            if (!query.IncludeUnconfirmed) source = source.Where(item => item.Direction == MessageDirection.Incoming || item.Delivery == DeliveryState.Confirmed);
            long? from = query.From?.ToUnixTimeMilliseconds();
            long? until = query.Until?.ToUnixTimeMilliseconds();
            bool forward = query.PageDirection == PageDirection.Forward;
            // 两种时间列分别构建表达式，使 SQLite 可以直接选择相应组合索引。
            if (query.Time == HistoryTime.Received)
            {
                if (from.HasValue) source = source.Where(item => item.ReceivedAt >= from.Value);
                if (until.HasValue) source = source.Where(item => item.ReceivedAt < until.Value);
                if (query.Cursor is not null)
                {
                    long time = query.Cursor.UnixMilliseconds;
                    long id = query.Cursor.MessageId;
                    source = forward
                        ? source.Where(item => item.ReceivedAt > time || (item.ReceivedAt == time && item.Id > id))
                        : source.Where(item => item.ReceivedAt < time || (item.ReceivedAt == time && item.Id < id));
                }
                source = forward ? source.OrderBy(item => item.ReceivedAt).ThenBy(item => item.Id)
                    : source.OrderByDescending(item => item.ReceivedAt).ThenByDescending(item => item.Id);
            }
            else
            {
                source = source.Where(item => item.OccurredAt.HasValue);
                if (from.HasValue) source = source.Where(item => item.OccurredAt >= from.Value);
                if (until.HasValue) source = source.Where(item => item.OccurredAt < until.Value);
                if (query.Cursor is not null)
                {
                    long time = query.Cursor.UnixMilliseconds;
                    long id = query.Cursor.MessageId;
                    source = forward
                        ? source.Where(item => item.OccurredAt > time || (item.OccurredAt == time && item.Id > id))
                        : source.Where(item => item.OccurredAt < time || (item.OccurredAt == time && item.Id < id));
                }
                source = forward ? source.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
                    : source.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id);
            }
            List<MessageEntity> rows = source.Include(item => item.Segments).ThenInclude(item => item.Media).Take(query.PageSize + 1).ToList();
            bool more = rows.Count > query.PageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            MessageEntity? last = rows.LastOrDefault();
            return new HistoryPage
            {
                Messages = rows.Select(Snapshot).ToImmutableArray(),
                NextCursor = more && last is not null ? new HistoryCursor
                { UnixMilliseconds = query.Time == HistoryTime.Received ? last.ReceivedAt : last.OccurredAt!.Value, MessageId = last.Id } : null
            };
        }, cancellationToken);
    }

    /// <summary>按接收标识读取同会话邻近记录，结果按先后排列且包含中心消息。</summary>
    public Task<ImmutableArray<MessageSnapshot>> GetAroundAsync(long id, int before, int after, CancellationToken cancellationToken = default)
    {
        if (before is < 0 or > 50 || after is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(before), "邻近数量必须在 0 至 50 之间。");
        return ReadAsync(database =>
        {
            MessageEntity center = database.Messages.AsNoTracking().Include(item => item.Segments).ThenInclude(item => item.Media).Single(item => item.Id == id);
            center = ResolveCanonical(database, center);
            center = WithSegments(database.Messages.AsNoTracking()).Single(item => item.Id == center.Id);
            id = center.Id;
            IQueryable<MessageEntity> source = InConversation(database.Messages.AsNoTracking(), Address(center)).Where(item => item.CanonicalId == null);
            List<MessageEntity> previous = source.Where(item => item.Id < id).OrderByDescending(item => item.Id).Take(before).Include(item => item.Segments).ThenInclude(item => item.Media).ToList();
            previous.Reverse();
            previous.Add(center);
            previous.AddRange(source.Where(item => item.Id > id).OrderBy(item => item.Id).Take(after).Include(item => item.Segments).ThenInclude(item => item.Media).ToList());
            return previous.Select(Snapshot).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>在固定读工作循环中创建并释放上下文。</summary>
    private Task<T> ReadAsync<T>(Func<MissiliDbContext, T> operation, CancellationToken token) => _reads.ExecuteAsync(() =>
    {
        using MissiliDbContext database = new(_options);
        return operation(database);
    }, token);

    /// <summary>在单写工作循环中创建并释放上下文。</summary>
    private Task<T> WriteAsync<T>(Func<MissiliDbContext, T> operation, CancellationToken token) => _writes.ExecuteAsync(() =>
    {
        using MissiliDbContext database = new(_options);
        return operation(database);
    }, token);

    /// <summary>构造来源字段，避免不同平台会话的标识相互混淆。</summary>
    private static MessageEntity CreateMessage(MessageAddress address, string sender, DateTimeOffset now, Guid runId) => new()
    {
        AdapterId = address.AdapterId,
        PlatformId = address.PlatformId,
        BotAccountId = address.BotAccountId,
        ConversationKind = address.ConversationKind,
        ConversationId = address.ConversationId,
        SenderId = sender,
        ReceivedAt = now.ToUnixTimeMilliseconds(),
        RunId = runId
    };

    /// <summary>将来源字段转换为不可变地址。</summary>
    private static MessageAddress Address(MessageEntity entity) => new()
    {
        AdapterId = entity.AdapterId,
        PlatformId = entity.PlatformId,
        BotAccountId = entity.BotAccountId,
        ConversationKind = entity.ConversationKind,
        ConversationId = entity.ConversationId
    };

    /// <summary>所有会话查询使用完整地址，不依赖仅群号或用户号。</summary>
    private static IQueryable<MessageEntity> InConversation(IQueryable<MessageEntity> source, MessageAddress address) => source.Where(item =>
        item.AdapterId == address.AdapterId && item.BotAccountId == address.BotAccountId && item.PlatformId == address.PlatformId &&
        item.ConversationKind == address.ConversationKind && item.ConversationId == address.ConversationId);

    /// <summary>复制有序内容并转换时间，不向调用者泄露 EF 实体。</summary>
    private static MessageSnapshot Snapshot(MessageEntity entity) => new()
    {
        Id = entity.Id,
        Address = Address(entity),
        SenderId = entity.SenderId,
        PlatformMessageId = entity.PlatformMessageId,
        ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.ReceivedAt),
        OccurredAt = entity.OccurredAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(entity.OccurredAt.Value) : null,
        Direction = entity.Direction,
        Recalled = entity.Recalled,
        Deleted = entity.Deleted,
        Processing = entity.Processing,
        Delivery = entity.Delivery,
        Metadata = ParseJson(entity.MetadataJson),
        Segments = entity.Segments.OrderBy(item => item.Position).Select(item => new MessageSegment
        {
            Kind = item.Kind,
            Version = item.Version,
            Text = item.Text,
            Payload = ParseJson(item.PayloadJson),
            ParseState = item.ParseState,
            ParseError = item.ParseError,
            ContentId = item.ContentId,
            MediaIds = item.Media.OrderBy(link => link.Ordinal).Select(link => link.MediaId).ToImmutableArray()
        }).ToImmutableArray()
    };

    /// <summary>等待已入队读写结束；宿主必须先停止业务生产者。</summary>
    public async ValueTask DisposeAsync()
    {
        await _reads.DisposeAsync().ConfigureAwait(false);
        await _writes.DisposeAsync().ConfigureAwait(false);
    }
}
