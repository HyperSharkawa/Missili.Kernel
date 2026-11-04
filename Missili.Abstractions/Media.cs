using System.Text.Json;
using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>原件种类独立于消息段语义，数值保留既有媒体存储含义。</summary>
public enum MediaKind { Image = 1, Audio = 2, Video = 3, File = 4 }

/// <summary>可持久化的获取描述，稳定来源键与临时下载地址分别表达。</summary>
public sealed record MediaSource
{
    public required string Kind { get; init; }
    public int Version { get; init; } = 1;
    public required JsonElement Data { get; init; }
    public string? AdapterId { get; init; }
    public string? BotAccountId { get; init; }
    public string? StableKey { get; init; }
    /// <summary>从 HTTP 地址创建直接下载来源，不把地址当作稳定身份。</summary>
    public static MediaSource Http(Uri uri, string? stableKey = null) => new()
    { Kind = "http", Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["url"] = uri.AbsoluteUri }), StableKey = stableKey };
    /// <summary>验证可跨存储边界的描述，协议解析留在对应读取器中。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        if (Version != 1 || Data.ValueKind != JsonValueKind.Object) throw new ArgumentException("媒体来源版本或载荷无效。");
        if (Kind == "http" && (!Uri.TryCreate(Data.GetProperty("url").GetString(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("HTTP 媒体来源必须使用 HTTP(S)。");
    }
    /// <summary>取得 JSON 所有权，不保存外部文档引用。</summary>
    public MediaSource Freeze() => this with { Data = Data.Clone() };
}

/// <summary>接收段中的一个媒体来源，一个段可以包含多个原件。</summary>
public sealed record IncomingMedia
{
    public required MediaKind Kind { get; init; }
    public required MediaSource Source { get; init; }
}

/// <summary>原件归档状态，完成文件操作与提交数据库之间保留独立状态。</summary>
public enum MediaState { Pending, Downloading, Finalizing, Available, Failed, Cleaned, WaitingForAdapter }

/// <summary>公开媒体状态；不向插件返回原始下载地址及本地路径。</summary>
public sealed record MediaStatus
{
    public required Guid Id { get; init; }
    public required MediaKind Kind { get; init; }
    public required MediaState State { get; init; }
    public string? ContentKey { get; init; }
    public long? Length { get; init; }
    public string? Error { get; init; }
    public JsonElement? Representation { get; init; }
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

/// <summary>插件生成的媒体可直接导入宿主归档，不必构造虚假的入站消息。</summary>
public interface IMediaImporter
{
    /// <summary>流式导入并完成文件提交，返回独立媒体引用；输入流仍由调用者释放。</summary>
    Task<MediaStatus> ImportAsync(MediaKind kind, Stream content, JsonElement? representation = null, CancellationToken cancellationToken = default);
}

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
