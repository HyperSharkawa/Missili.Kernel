using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>短事务捕获的上下文历史，不在网络调用期间持有会话锁。</summary>
public sealed record ConversationWindow
{
    public required long HighWatermark { get; init; }
    public required long DiscardedThrough { get; init; }
    public required long Revision { get; init; }
    public required ImmutableArray<MessageSnapshot> Messages { get; init; }
}

/// <summary>逻辑模型调用记录与实际候选尝试分别保存。</summary>
public sealed record ModelCallRecord
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required string Purpose { get; init; }
    public OperationOrigin? Origin { get; init; }
    public required string ProfileId { get; init; }
    public long ConfigurationRevision { get; init; }
    public MessageAddress? Conversation { get; init; }
    public long? TriggerMessageId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public ModelCallState State { get; init; }
    public string? ModelId { get; init; }
    public string? Error { get; init; }
    public ImmutableArray<ModelAttemptSummary> Attempts { get; init; } = [];
    public ImmutableArray<ModelCandidateExclusion> ExcludedCandidates { get; init; } = [];
}

/// <summary>调用记录按时间和唯一序号翻页，不将未知用量估算为零。</summary>
public sealed record ModelCallQuery
{
    public MessageAddress? Conversation { get; init; }
    public string? Purpose { get; init; }
    public ModelCallState? State { get; init; }
    public DateTimeOffset? StartedFrom { get; init; }
    public DateTimeOffset? StartedUntil { get; init; }
    public long? BeforeSequence { get; init; }
    public int PageSize { get; init; } = 100;
}

/// <summary>有限调用历史页，顺序按库内接收序号倒序。</summary>
public sealed record ModelCallPage
{
    public required ImmutableArray<ModelCallRecord> Calls { get; init; }
    public long? NextSequence { get; init; }
}

/// <summary>排障详情使用独占只读流，避免一次读取把大响应全部放入内存。</summary>
public sealed class ModelDetailRead : IAsyncDisposable
{
    public required Stream Content { get; init; }
    public required string ContentType { get; init; }
    /// <summary>释放当前调用的文件句柄。</summary>
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>插件可查询的调用历史，不提供数据库实体或任意文件路径。</summary>
public interface IModelCallHistory
{
    /// <summary>读取一个调用及它的候选尝试。</summary>
    Task<ModelCallRecord?> GetCallAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>分页查询调用元数据。</summary>
    Task<ModelCallPage> QueryCallsAsync(ModelCallQuery query, CancellationToken cancellationToken = default);
    /// <summary>打开某次尝试的诊断详情，调用者负责释放。</summary>
    Task<ModelDetailRead?> OpenDetailAsync(Guid attemptId, CancellationToken cancellationToken = default);
}
