using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

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
