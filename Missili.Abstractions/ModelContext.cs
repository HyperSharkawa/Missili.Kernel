using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>一次会话调用的历史和配置快照，候选切换期间不重新查询聊天历史。</summary>
public sealed record ConversationContextSnapshot
{
    public required ConversationProfileSnapshot Profile { get; init; }
    public required MessageAddress Address { get; init; }
    public long? TriggerMessageId { get; init; }
    public required ConversationWindow Window { get; init; }
}

/// <summary>按候选能力投影固定会话资料，不覆盖原始持久化内容。</summary>
public interface IConversationContextService
{
    /// <summary>捕获历史并用当前处理内容替换触发消息的投影。</summary>
    Task<ConversationContextSnapshot> CaptureAsync(MessageContext message, CancellationToken cancellationToken = default);
    /// <summary>为一个候选准备输入；等待转述时尚未占用模型网络额度。</summary>
    Task<ImmutableArray<ModelMessage>> ProjectAsync(ConversationContextSnapshot context, ModelDefinition model, CancellationToken cancellationToken = default);
}

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

/// <summary>模型输入需要的媒体表示，派生文件继续由宿主媒体存储管理。</summary>
public sealed record MediaRepresentation
{
    public required Guid MediaId { get; init; }
    public required string MimeType { get; init; }
    public required string OriginalContentKey { get; init; }
}

/// <summary>格式准备通过适配器能力取得派生文件，不把重命名当作转码。</summary>
public interface IMediaRepresentationService
{
    /// <summary>返回候选可用的原件或 WAV 派生，失败明确说明。</summary>
    Task<MediaRepresentation> PrepareAudioAsync(Guid mediaId, ModelDefinition model, CancellationToken cancellationToken = default);
}
