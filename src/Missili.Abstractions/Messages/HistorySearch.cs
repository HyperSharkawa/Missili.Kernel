using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>普通检索只包含可展示聊天、明确要求、成果与工具正文。</summary>
public enum HistoryRecordKind { Chat, AgentInput, AgentResult, ToolText }

/// <summary>稳定时间和唯一投影身份共同决定下一页，不使用偏移页码。</summary>
public sealed record HistorySearchCursor
{
    public required DateTimeOffset At { get; init; }
    public required long Id { get; init; }
}

/// <summary>固定字段的字面检索，不接受 SQL、正则或全文搜索表达式。</summary>
public sealed record HistorySearchQuery
{
    public required MessageAddress Conversation { get; init; }
    public required string Text { get; init; }
    public string? SenderId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public ImmutableArray<HistoryRecordKind> Kinds { get; init; } = [];
    public MessageVisibilityPolicy Visibility { get; init; } = MessageVisibilityPolicy.Placeholder;
    public int PageSize { get; init; } = 20;
    public HistorySearchCursor? Before { get; init; }
}

/// <summary>搜索命中提供有限原文片段及可展开身份，不冒充全文或自动摘要。</summary>
public sealed record HistorySearchHit
{
    public required long Id { get; init; }
    public required HistoryRecordKind Kind { get; init; }
    public required string SourceId { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? SenderId { get; init; }
    public Guid? TaskId { get; init; }
    public Guid? AgentId { get; init; }
    public long? MessageId { get; init; }
    public required string Text { get; init; }
    public bool Partial { get; init; }
    public bool HiddenOriginal { get; init; }
}

/// <summary>有限搜索页；游标继续同一条件下的稳定顺序。</summary>
public sealed record HistorySearchPage
{
    public required ImmutableArray<HistorySearchHit> Hits { get; init; }
    public HistorySearchCursor? Next { get; init; }
}

/// <summary>范围与可见性明确的历史搜索服务，插件不能自定义查询语言。</summary>
public interface IHistorySearch
{
    /// <summary>按字面关键词、时间、发言者和记录种类返回有限片段。</summary>
    Task<HistorySearchPage> SearchAsync(HistorySearchQuery query, CancellationToken cancellationToken = default);
}
