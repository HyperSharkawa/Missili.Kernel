using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>转述执行结果与成功缓存分离，失败可携带已取得的部分说明。</summary>
public sealed record MediaTranscriptionResult
{
    public TranscriptionSnapshot? Cached { get; init; }
    public string? PartialText { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Cached?.Text is not null && Error is null;
}

/// <summary>框架负责共享工作和缓存，业务插件取消只结束自身等待。</summary>
public interface IMediaTranscriptionService
{
    /// <summary>查询已有成功转述和最近失败，不启动网络或重试。</summary>
    Task<TranscriptionSnapshot?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default);
    /// <summary>确保完成转述或显式刷新；成功才替换当前缓存。</summary>
    Task<MediaTranscriptionResult> EnsureAsync(Guid mediaId, ConversationProfileSnapshot profile, bool refresh = false, CancellationToken cancellationToken = default);
}
