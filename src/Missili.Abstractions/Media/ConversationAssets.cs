namespace Missili.Abstractions;

/// <summary>模型读取的归档种类，不能通过字符串路径选择任意文件。</summary>
public enum ConversationAssetKind { Media, Content }

/// <summary>媒体和转发引用以真实消息关联核对会话范围与撤回可见性。</summary>
public interface IConversationAssetAccess
{
    /// <summary>仅认可当前会话中的可见来源，已知 Guid 不构成跨会话授权。</summary>
    Task<bool> CanReadAsync(MessageAddress conversation, ConversationAssetKind kind, Guid id, MessageVisibilityPolicy visibility, CancellationToken cancellationToken = default);
}
