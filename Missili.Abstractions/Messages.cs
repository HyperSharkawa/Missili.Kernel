using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>与具体平台协议无关的消息段种类。</summary>
public enum SegmentKind { Text, Image, Audio, Video }

/// <summary>区分收到的消息与宿主发送意图。</summary>
public enum MessageDirection { Incoming, Outgoing }

/// <summary>消息业务执行状态；中断记录不会在重启后重新执行。</summary>
public enum ProcessingState { Pending, Running, Finished, Interrupted }

/// <summary>发送结果独立于插件执行结果，未知结果不能自动重发。</summary>
public enum DeliveryState { NotApplicable, Pending, Sending, Confirmed, Failed, Unknown, Cancelled }

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

/// <summary>媒体获取描述；稳定来源键由适配器提供，不根据临时地址猜测。</summary>
public sealed record MediaSource
{
    public required Uri Uri { get; init; }
    public string? StableKey { get; init; }
}

/// <summary>接收时的消息段；媒体内容由宿主另行归档。</summary>
public sealed record IncomingSegment
{
    public required SegmentKind Kind { get; init; }
    public string? Text { get; init; }
    public MediaSource? Source { get; init; }
}

/// <summary>持久化后的不可变消息段，媒体通过稳定引用访问。</summary>
public sealed record MessageSegment
{
    public required SegmentKind Kind { get; init; }
    public string? Text { get; init; }
    public Guid? MediaId { get; init; }
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
            if (!Enum.IsDefined(segment.Kind)) throw new ArgumentException("不支持的消息段类型。");
            // 文本与原件来源互斥，避免存入无法解释的混合载荷。
            if (segment.Kind == SegmentKind.Text)
            {
                if (segment.Text is null || segment.Source is not null) throw new ArgumentException("文本段载荷无效。");
            }
            else
            {
                if (segment.Text is not null || segment.Source is null || !segment.Source.Uri.IsAbsoluteUri ||
                    segment.Source.Uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException("媒体段必须提供 HTTP(S) 来源。");
            }
        }
    }
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
    public SegmentKind? SegmentKind { get; init; }
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
