using System.Text.Json;
using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>当前成功转述及独立失败记录，模型变化不自动使其失效。</summary>
public sealed record TranscriptionSnapshot
{
    public required string ContentKey { get; init; }
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public ImmutableArray<TranscriptionSource> Sources { get; init; } = [];
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public long Revision { get; init; }
}

/// <summary>准确记录每批转述的来源，缓存身份仍然只取原件与转述种类。</summary>
public sealed record TranscriptionSource
{
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Prompt { get; init; }
    public int Batch { get; init; }
    public Guid? CallId { get; init; }
}

/// <summary>转述成功缓存与修订存储；模型执行由转述服务负责。</summary>
public interface IMediaTranscriptionStore
{
    /// <summary>读取指定内容和转述类型的缓存。</summary>
    Task<TranscriptionSnapshot?> GetTranscriptionAsync(string contentKey, string kind, CancellationToken cancellationToken = default);
    /// <summary>按预期修订号提交成功结果，过期更新抛出冲突错误。</summary>
    Task<TranscriptionSnapshot> SaveTranscriptionAsync(TranscriptionSnapshot result, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>记录失败尝试，不替换已有成功文本。</summary>
    Task<TranscriptionSnapshot> RecordTranscriptionFailureAsync(string contentKey, string kind, string error, long expectedRevision, CancellationToken cancellationToken = default);
}
