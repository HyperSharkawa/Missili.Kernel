using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>常用段的稳定标识；扩展段使用自己的命名空间，不需要修改枚举或数据表。</summary>
public static class SegmentTypes
{
    public const string Text = "text", Image = "image", Audio = "audio", Video = "video";
    public const string Mention = "mention", Reply = "reply", Share = "share", Location = "location", Forward = "forward";
    public const string Face = "qq.face", Contact = "qq.contact", Music = "qq.music", Rps = "qq.rps", Dice = "qq.dice";
    public const string Poke = "qq.poke", Shake = "qq.shake", Anonymous = "qq.anonymous";
    public const string Json = "onebot11.json", Xml = "onebot11.xml";
}

/// <summary>区分已解释、未知扩展和已知格式错误，不通过文本占位掩盖原始数据。</summary>
public enum SegmentParseState { Parsed, Unrecognized, Invalid }

/// <summary>区分收到的消息与宿主发送意图。</summary>
public enum MessageDirection { Incoming, Outgoing }

/// <summary>消息业务执行状态；中断记录不会在重启后重新执行。</summary>
public enum ProcessingState { Pending, Running, Finished, Interrupted }

/// <summary>发送结果独立于插件执行结果，未知结果不能自动重发。</summary>
public enum DeliveryState { NotApplicable, Pending, Sending, Confirmed, Failed, Unknown, Cancelled, Accepted }

/// <summary>历史时间筛选依据。</summary>
public enum HistoryTime { Received, Occurred }

/// <summary>游标之前或之后的一页记录。</summary>
public enum PageDirection { Forward, Backward }

/// <summary>完整目标地址，避免不同账号或平台的会话标识发生碰撞。</summary>
public sealed record MessageAddress
{
    public required string AdapterId { get; init; }
    public required string PlatformId { get; init; }
    public required string BotAccountId { get; init; }
    public required string ConversationKind { get; init; }
    public required string ConversationId { get; init; }

    /// <summary>校验来源及目标字段；非法地址立即抛出参数错误。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PlatformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BotAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConversationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConversationId);
    }
}

/// <summary>接收时的消息段；媒体内容由宿主另行归档。</summary>
public sealed record IncomingSegment
{
    public required string Kind { get; init; }
    public int Version { get; init; } = 1;
    public string? Text { get; init; }
    public JsonElement? Payload { get; init; }
    public SegmentParseState ParseState { get; init; }
    public string? ParseError { get; init; }
    public ImmutableArray<IncomingMedia> Media { get; init; } = [];
    public ContentSource? Content { get; init; }

    /// <summary>复制 JSON 所有权，调用者释放原文档后排队消息仍有效。</summary>
    public IncomingSegment Freeze() => this with
    {
        Payload = Payload?.Clone(),
        Media = Media.Select(item => item with { Source = item.Source.Freeze() }).ToImmutableArray(),
        Content = Content?.Freeze()
    };
}

/// <summary>持久化后的不可变消息段，媒体通过稳定引用访问。</summary>
public sealed record MessageSegment
{
    public required string Kind { get; init; }
    public int Version { get; init; } = 1;
    public string? Text { get; init; }
    public JsonElement? Payload { get; init; }
    public SegmentParseState ParseState { get; init; }
    public string? ParseError { get; init; }
    public ImmutableArray<Guid> MediaIds { get; init; } = [];
    public Guid? ContentId { get; init; }

    /// <summary>复制扩展数据，快照不会借用调用者的 JSON 文档。</summary>
    public MessageSegment Freeze() => this with { Payload = Payload?.Clone() };
}

/// <summary>平台适配器向内核提交的通用消息，不包含任何 QQ 专有字段。</summary>
public sealed record IncomingMessage
{
    public required MessageAddress Address { get; init; }
    public required string SenderId { get; init; }
    public MessageDirection Direction { get; init; } = MessageDirection.Incoming;
    public string? PlatformMessageId { get; init; }
    public string? DeduplicationKey { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public JsonElement? Metadata { get; init; }
    public required ImmutableArray<IncomingSegment> Segments { get; init; }

    /// <summary>在进入持久化队列前校验消息及每个段的互斥载荷。</summary>
    public void Validate()
    {
        Address.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(SenderId);
        if (!Enum.IsDefined(Direction)) throw new ArgumentException("消息方向无效。");
        if (Segments.IsDefaultOrEmpty) throw new ArgumentException("消息必须至少包含一个消息段。");
        foreach (IncomingSegment segment in Segments)
        {
            ContentValidation.ValidateSegment(segment.Kind, segment.Version, segment.Text, segment.Payload, segment.ParseState);
            if (segment.Media.IsDefault) throw new ArgumentException("媒体集合未初始化。");
            foreach (IncomingMedia media in segment.Media) { media.Source.Validate(); if (!Enum.IsDefined(media.Kind)) throw new ArgumentException("媒体种类无效。"); }
            segment.Content?.Validate();
        }
    }

    /// <summary>在跨异步接收边界前取得完整载荷所有权。</summary>
    public IncomingMessage Freeze() => this with { Metadata = Metadata?.Clone(), Segments = Segments.Select(item => item.Freeze()).ToImmutableArray() };
}

/// <summary>与实体跟踪脱离的历史快照，原始内容不会被插件修改。</summary>
public sealed record MessageSnapshot
{
    public required long Id { get; init; }
    public required MessageAddress Address { get; init; }
    public required string SenderId { get; init; }
    public string? PlatformMessageId { get; init; }
    public required MessageDirection Direction { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public bool Recalled { get; init; }
    public bool Deleted { get; init; }
    public ProcessingState Processing { get; init; }
    public DeliveryState Delivery { get; init; }
    public JsonElement? Metadata { get; init; }
    public required ImmutableArray<MessageSegment> Segments { get; init; }
}

/// <summary>稳定游标包含时间和库内标识，允许相同时间戳的消息完整翻页。</summary>
public sealed record HistoryCursor
{
    public required long UnixMilliseconds { get; init; }
    public required long MessageId { get; init; }
}

/// <summary>有限历史查询；可见性和时间依据必须由调用者明确选择。</summary>
public sealed record HistoryQuery
{
    public MessageAddress? Address { get; init; }
    public string? SenderId { get; init; }
    public MessageDirection? Direction { get; init; }
    public string? SegmentType { get; init; }
    public required HistoryTime Time { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public required bool IncludeRecalled { get; init; }
    public required bool IncludeDeleted { get; init; }
    public required bool IncludeUnconfirmed { get; init; }
    public PageDirection PageDirection { get; init; }
    public HistoryCursor? Cursor { get; init; }
    public int PageSize { get; init; } = 100;
}

/// <summary>一页历史及继续读取所需的游标。</summary>
public sealed record HistoryPage
{
    public required ImmutableArray<MessageSnapshot> Messages { get; init; }
    public HistoryCursor? NextCursor { get; init; }
}

/// <summary>持久化回执；重复消息不重新分发，失败信息不包含输入正文。</summary>
public sealed record ReceiveReceipt
{
    public MessageSnapshot? Message { get; init; }
    public bool Duplicate { get; init; }
    public string? Error { get; init; }
}

/// <summary>接收入口，成功回执只承诺历史已持久化。</summary>
public interface IMessageIngress
{
    /// <summary>排队保存消息；取消令牌约束提交等待，不重放已经提交的消息。</summary>
    Task<ReceiveReceipt> ReceiveAsync(IncomingMessage message, CancellationToken cancellationToken = default);
}

/// <summary>插件的历史读取边界，不暴露数据库上下文。</summary>
public interface IMessageHistory
{
    /// <summary>按筛选条件查询一页历史，结果为不可变快照。</summary>
    Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    /// <summary>读取指定记录；不存在时返回空。</summary>
    Task<MessageSnapshot?> GetAsync(long id, CancellationToken cancellationToken = default);
    /// <summary>读取同会话中指定消息及其前后邻近记录；前后数量各不超过 50。</summary>
    Task<ImmutableArray<MessageSnapshot>> GetAroundAsync(long id, int before, int after, CancellationToken cancellationToken = default);
}
