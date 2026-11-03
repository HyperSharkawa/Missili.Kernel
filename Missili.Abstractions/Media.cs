namespace Missili.Abstractions;

/// <summary>原件归档状态，完成文件操作与提交数据库之间保留独立状态。</summary>
public enum MediaState { Pending, Downloading, Finalizing, Available, Failed, Cleaned }

/// <summary>公开媒体状态；不向插件返回原始下载地址及本地路径。</summary>
public sealed record MediaStatus
{
    public required Guid Id { get; init; }
    public required SegmentKind Kind { get; init; }
    public required MediaState State { get; init; }
    public string? ContentKey { get; init; }
    public long? Length { get; init; }
    public string? Error { get; init; }
}

/// <summary>媒体读取结果；成功流由调用者独占并负责释放。</summary>
public sealed class MediaReadResult : IDisposable, IAsyncDisposable
{
    public required MediaStatus Status { get; init; }
    public Stream? Content { get; init; }
    public string? Error { get; init; }

    /// <summary>释放本次读取持有的流，不影响其他读取者。</summary>
    public void Dispose() => Content?.Dispose();
    /// <summary>异步释放本次流；返回释放完成状态。</summary>
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>异步原件访问，不负责推断插件是否需要媒体。</summary>
public interface IMediaStore
{
    /// <summary>读取媒体状态；不存在时返回空。</summary>
    Task<MediaStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>等待已有归档并打开原件；调用者取消不会终止宿主归档。</summary>
    Task<MediaReadResult> OpenReadAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>当前成功转述及独立失败记录，模型变化不自动使其失效。</summary>
public sealed record TranscriptionSnapshot
{
    public required string ContentKey { get; init; }
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Prompt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public long Revision { get; init; }
}

/// <summary>转述存储契约；本阶段不会调用任何转述模型。</summary>
public interface IMediaTranscriptionStore
{
    /// <summary>读取指定内容和转述类型的缓存。</summary>
    Task<TranscriptionSnapshot?> GetTranscriptionAsync(string contentKey, string kind, CancellationToken cancellationToken = default);
    /// <summary>按预期修订号提交成功结果，过期更新抛出冲突错误。</summary>
    Task<TranscriptionSnapshot> SaveTranscriptionAsync(TranscriptionSnapshot result, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>记录失败尝试，不替换已有成功文本。</summary>
    Task<TranscriptionSnapshot> RecordTranscriptionFailureAsync(string contentKey, string kind, string error, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>并发修改使用的明确冲突，调用者不能把它当作成功。</summary>
public sealed class RevisionConflictException : InvalidOperationException
{
    /// <summary>创建可读冲突信息，不包含用户内容。</summary>
    public RevisionConflictException() : base("记录已被其他操作修改，请重新读取后提交。") { }
}
