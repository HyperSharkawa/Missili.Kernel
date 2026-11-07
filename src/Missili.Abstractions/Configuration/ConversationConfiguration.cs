using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>独立配置档，未配置可选项仅使用程序默认值。</summary>
public sealed record ConversationProfile
{
    public required string Id { get; init; }
    public AgentProfile Agent { get; init; } = new();
    public ToolApprovalOptions Approvals { get; init; } = new();
    public OutgoingOptions Outgoing { get; init; } = new();
    public ImmutableDictionary<string, JsonElement> Plugins { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public ImmutableArray<string> ImageModels { get; init; } = [];
    public ImmutableArray<string> TranscriptionModels { get; init; } = [];
    public string ImagePrompt { get; init; } = "准确描述图片内容和可读文字，按所给位置顺序描述，不猜测看不清的内容。";
    public string ImagePromptVersion { get; init; } = "1";
    public int MaximumMessages { get; init; } = 100;
    public int DiscardMessages { get; init; } = 20;
    public MessageVisibilityPolicy Visibility { get; init; } = MessageVisibilityPolicy.MarkedOriginal;
    public ModelMediaPolicy Images { get; init; } = ModelMediaPolicy.OriginalPreferred;
    public ModelMediaPolicy Audio { get; init; } = ModelMediaPolicy.OriginalPreferred;
    public bool ImageTranscriptionEnabled { get; init; }
    public bool AudioTranscriptionEnabled { get; init; }
    public long MaxAudioBytes { get; init; } = 24 * 1024 * 1024;
    public ModelExecutionOptions Execution { get; init; } = new();
    public ImagePreparationOptions ImagePreparation { get; init; } = new();
}

/// <summary>完整会话身份上的首匹配路由，支持星号和问号。</summary>
public sealed record ConversationRoute
{
    public string AdapterId { get; init; } = "*";
    public string PlatformId { get; init; } = "*";
    public string BotAccountId { get; init; } = "*";
    public string ConversationKind { get; init; } = "*";
    public string ConversationId { get; init; } = "*";
    public required string ProfileId { get; init; }
}

/// <summary>一次原子发布的目录和配置文档，所有引用同时校验。</summary>
public sealed record ConversationConfiguration
{
    public required string DefaultProfileId { get; init; }
    public ImmutableArray<ModelProviderDefinition> Providers { get; init; } = [];
    public ImmutableArray<ModelDefinition> Models { get; init; } = [];
    public ImmutableArray<ConversationProfile> Profiles { get; init; } = [];
    public ImmutableArray<ConversationRoute> Routes { get; init; } = [];
}

/// <summary>配置快照由一次调用持有，后续发布不改变候选或提示词。</summary>
public sealed record ConversationProfileSnapshot
{
    public required long Revision { get; init; }
    public required ConversationProfile Profile { get; init; }
    public required ImmutableDictionary<string, ModelDefinition> Models { get; init; }
    public required ImmutableDictionary<string, ModelProviderDefinition> Providers { get; init; }
}

/// <summary>会话路由和文件重载共享同一份原子配置。</summary>
public interface IConversationProfileService
{
    /// <summary>加载插件时校验全部配置档，快照不包含未发布的文件状态。</summary>
    ImmutableArray<ConversationProfileSnapshot> CaptureProfiles();
    /// <summary>基础设施未启用配置时返回空值，普通无模型发送继续使用程序默认行为。</summary>
    ConversationProfileSnapshot? TryResolve(MessageAddress address);
    /// <summary>按完整地址返回当前配置快照。</summary>
    ConversationProfileSnapshot Resolve(MessageAddress address);
    /// <summary>重新读取配置，失败抛错并保持上次有效快照。</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
