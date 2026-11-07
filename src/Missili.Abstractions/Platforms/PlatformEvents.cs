using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>业务事件类别；连接和心跳不进入长期业务事件表。</summary>
public enum PlatformEventCategory { Notice, Request, Observation }

/// <summary>平台业务事件的纯值描述，会话可空且不能伪装成用户发言。</summary>
public sealed record IncomingPlatformEvent
{
    public required string AdapterId { get; init; }
    public required string PlatformId { get; init; }
    public required string BotAccountId { get; init; }
    public required string Type { get; init; }
    public string? SubType { get; init; }
    public PlatformEventCategory Category { get; init; }
    public string? ConversationKind { get; init; }
    public string? ConversationId { get; init; }
    public string? ActorId { get; init; }
    public string? TargetUserId { get; init; }
    public string? TargetMessageId { get; init; }
    public string? DeduplicationKey { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public JsonElement? Payload { get; init; }
    public bool RecallsMessage { get; init; }
    /// <summary>验证明确的身份和成对会话字段，不合成缺失字段。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PlatformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BotAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Type);
        if (!Enum.IsDefined(Category) || (ConversationKind is null) != (ConversationId is null)) throw new ArgumentException("事件类别或会话无效。");
        if (RecallsMessage && (TargetMessageId is null || ConversationId is null)) throw new ArgumentException("撤回事件必须指明会话及目标消息。");
    }
    /// <summary>复制载荷所有权，避免原文档释放影响持久化。</summary>
    public IncomingPlatformEvent Freeze() => this with { Payload = Payload?.Clone() };
}

/// <summary>已持久化事件及分发状态。</summary>
public sealed record PlatformEventSnapshot
{
    public required long Id { get; init; }
    public required IncomingPlatformEvent Event { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public ProcessingState Processing { get; init; }
}

/// <summary>事件保存回执，重复只依据可靠的外部去重键。</summary>
public sealed record EventReceipt
{
    public PlatformEventSnapshot? Event { get; init; }
    public bool Duplicate { get; init; }
    public string? Error { get; init; }
}

/// <summary>受限事件历史查询，按接收时间和本地标识稳定翻页。</summary>
public sealed record EventQuery
{
    public string? AdapterId { get; init; }
    public string? BotAccountId { get; init; }
    public string? Type { get; init; }
    public string? ActorId { get; init; }
    public string? ConversationKind { get; init; }
    public string? ConversationId { get; init; }
    public PlatformEventCategory? Category { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public HistoryCursor? Cursor { get; init; }
    public PageDirection PageDirection { get; init; }
    public int PageSize { get; init; } = 100;
}

/// <summary>事件历史的一页与继续查询游标。</summary>
public sealed record EventPage
{
    public required ImmutableArray<PlatformEventSnapshot> Events { get; init; }
    public HistoryCursor? NextCursor { get; init; }
}

/// <summary>业务事件接收边界，提交成功不代表监听器执行完成。</summary>
public interface IPlatformEventIngress
{
    /// <summary>持久化事件后调度插件，不将事件转成文本消息。</summary>
    Task<EventReceipt> ReceiveEventAsync(IncomingPlatformEvent input, CancellationToken cancellationToken = default);
}

/// <summary>平台事件的独立历史读取服务。</summary>
public interface IPlatformEventHistory
{
    /// <summary>读取指定事件，未找到时返回空。</summary>
    Task<PlatformEventSnapshot?> GetEventAsync(long id, CancellationToken cancellationToken = default);
    /// <summary>在数据库中筛选和分页，不暴露任意 JSON 查询。</summary>
    Task<EventPage> QueryEventsAsync(EventQuery query, CancellationToken cancellationToken = default);
}
