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
