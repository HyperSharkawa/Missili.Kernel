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

/// <summary>归档后的媒体及所属会话，用于有限扫描；不保存转述执行状态。</summary>
public sealed record TranscriptionMedia
{
    public required Guid MediaId { get; init; }
    public required MessageAddress Address { get; init; }
    public required MediaKind Kind { get; init; }
    public long Sequence { get; init; }
}

/// <summary>媒体扫描按消息翻页，空归档仍可推进游标而无需伪造媒体引用。</summary>
public sealed record TranscriptionMediaPage
{
    public required ImmutableArray<TranscriptionMedia> Items { get; init; }
    public long? NextMessageId { get; init; }
}

/// <summary>模型模块与持久化的内部边界，所有写入复用现有单写执行器。</summary>
public interface IModelStore
{
    /// <summary>原子推进批量淘汰边界并读取固定高水位内的历史。</summary>
    Task<ConversationWindow> CaptureWindowAsync(MessageAddress address, int maximum, int discard, CancellationToken cancellationToken);
    /// <summary>在首次网络请求前登记逻辑调用。</summary>
    Task CreateCallAsync(ModelCallRecord call, CancellationToken cancellationToken);
    /// <summary>保存逻辑调用结束状态，不因此重新发起模型请求。</summary>
    Task FinishCallAsync(Guid id, ModelResult result, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>保存一次尝试开始或结束的数据，标识固定且顺序唯一。</summary>
    Task SaveAttemptAsync(Guid callId, ModelAttemptSummary attempt, CancellationToken cancellationToken);
    /// <summary>读取逻辑调用和尝试元数据。</summary>
    Task<ModelCallRecord?> GetCallAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>按明确的查询条件分页读取调用记录。</summary>
    Task<ModelCallPage> QueryCallsAsync(ModelCallQuery query, CancellationToken cancellationToken);
    /// <summary>读取当前尝试的持久化详情标识，不能接受调用者传入路径。</summary>
    Task<string?> GetAttemptDetailAsync(Guid attemptId, CancellationToken cancellationToken);
    /// <summary>查找某原件已经导入的派生表示。</summary>
    Task<Guid?> GetRepresentationAsync(string contentKey, string kind, CancellationToken cancellationToken);
    /// <summary>保存派生格式关联，并发生成时保留已存在的关联。</summary>
    Task<Guid> SaveRepresentationAsync(string contentKey, string kind, Guid mediaId, CancellationToken cancellationToken);
    /// <summary>按稳定序号读取有限媒体批次，是否需要转述由当前会话配置决定。</summary>
    Task<TranscriptionMediaPage> ScanTranscriptionMediaAsync(long after, int count, CancellationToken cancellationToken);
}
