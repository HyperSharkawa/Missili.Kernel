using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>消息段、原件和结构化内容的通用持久化映射。</summary>
public sealed partial class SqliteStore
{
    /// <summary>解析为独立 JSON 值，返回后不持有可被释放的文档。</summary>
    private static JsonElement? ParseJson(string? value)
    {
        if (value is null) return null;
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    /// <summary>加载消息段与媒体关联，筛选和分页仍在数据库执行。</summary>
    private static IQueryable<MessageEntity> WithSegments(IQueryable<MessageEntity> query) => query.Include(item => item.Segments).ThenInclude(item => item.Media);

    /// <summary>构造一个段及其独立媒体引用，内容查询只登记任务。</summary>
    private static SegmentEntity CreateSegment(MissiliDbContext database, IncomingSegment segment, MessageAddress address, int position, DateTimeOffset now)
    {
        SegmentEntity row = new()
        {
            Position = position,
            Kind = segment.Kind,
            Version = segment.Version,
            Text = segment.Text,
            PayloadJson = segment.Payload?.GetRawText(),
            ParseState = segment.ParseState,
            ParseError = segment.ParseError,
            ContentId = segment.Content is null ? null : EnsureContent(database, segment.Content, now).Id
        };
        for (int ordinal = 0; ordinal < segment.Media.Length; ordinal++)
        {
            Guid id = AddMedia(database, segment.Media[ordinal], address, now);
            row.Media.Add(new MediaBindingEntity { MediaId = id, Ordinal = ordinal });
        }
        return row;
    }

    /// <summary>登记原件获取而不访问网络，来源键始终限定在账号作用域中。</summary>
    private static Guid AddMedia(MissiliDbContext database, IncomingMedia media, MessageAddress address, DateTimeOffset now)
    {
        media.Source.Validate();
        Guid id = Guid.NewGuid();
        MediaSource source = media.Source with { AdapterId = media.Source.AdapterId ?? address.AdapterId, BotAccountId = media.Source.BotAccountId ?? address.BotAccountId };
        database.MediaReferences.Add(new MediaEntity
        {
            Id = id,
            Kind = media.Kind,
            SourceJson = JsonSerializer.Serialize(source),
            AdapterId = source.Kind == "http" ? null : source.AdapterId,
            SourceKey = source.StableKey is null ? null : JsonSerializer.Serialize(new[] { address.AdapterId, address.BotAccountId, source.StableKey }),
            CreatedAt = now.ToUnixTimeMilliseconds()
        });
        return id;
    }

    /// <summary>按精确来源复用归档记录，失败内容不会被查询自动重新入队。</summary>
    private static StructuredContentEntity EnsureContent(MissiliDbContext database, ContentSource source, DateTimeOffset now)
    {
        source.Validate();
        string key = JsonSerializer.Serialize(source);
        StructuredContentEntity? existing = database.StructuredContents.Local.FirstOrDefault(item => item.SourceKey == key)
            ?? database.StructuredContents.SingleOrDefault(item => item.SourceKey == key);
        if (existing is not null) return existing;
        StructuredContentEntity row = new()
        { Id = Guid.NewGuid(), SourceKey = key, SourceJson = key, AdapterId = source.Address.AdapterId, CreatedAt = now.ToUnixTimeMilliseconds() };
        database.StructuredContents.Add(row);
        return row;
    }

    /// <summary>把原本地标识解析到规范记录，不物理删除被引用的历史。</summary>
    private static MessageEntity ResolveCanonical(MissiliDbContext database, MessageEntity entity)
    {
        HashSet<long> visited = [entity.Id];
        while (entity.CanonicalId.HasValue)
        {
            if (!visited.Add(entity.CanonicalId.Value)) throw new InvalidDataException("消息身份关联存在循环。");
            entity = database.Messages.Single(item => item.Id == entity.CanonicalId.Value);
        }
        return entity;
    }

    /// <summary>查找先于原消息到达的撤回，只使用完整平台作用域。</summary>
    private static bool HasRecall(MissiliDbContext database, MessageEntity message) => message.PlatformMessageId is not null && database.PlatformEvents.Any(item =>
        item.RecallsMessage && item.AdapterId == message.AdapterId && item.PlatformId == message.PlatformId && item.BotAccountId == message.BotAccountId &&
        item.ConversationKind == message.ConversationKind && item.ConversationId == message.ConversationId && item.TargetMessageId == message.PlatformMessageId);

    /// <summary>按明确平台身份取得规范消息，供引用和回执关联。</summary>
    public Task<MessageSnapshot?> FindPlatformMessageAsync(MessageAddress address, string platformId, MessageDirection? direction, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        IQueryable<MessageEntity> query = InConversation(database.Messages.AsNoTracking(), address)
            .Where(item => item.CanonicalId == null && item.PlatformMessageId == platformId);
        if (direction.HasValue) query = query.Where(item => item.Direction == direction.Value);
        MessageEntity? row = WithSegments(query).OrderBy(item => item.Id).FirstOrDefault();
        return row is null ? null : Snapshot(row);
    }, cancellationToken);
}
