using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>归档访问从消息与节点的真实引用关系判断，不以内容键或可猜测身份充当授权。</summary>
public sealed partial class SqliteStore : IConversationAssetAccess
{
    /// <summary>原件读取或转述必须有当前会话的可见来源，缓存共享不扩大访问范围。</summary>
    public Task<bool> CanReadAsync(MessageAddress conversation, ConversationAssetKind kind, Guid id, MessageVisibilityPolicy visibility, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(visibility)) throw new ArgumentException("归档种类或可见性无效。");
        return ReadAsync(database =>
        {
            if (kind == ConversationAssetKind.Content) return ReadableContent(database, conversation, id, visibility);
            List<MessageEntity> direct = InConversation(database.Messages.AsNoTracking(), conversation)
                .Where(message => database.MediaBindings.Any(binding => binding.MediaId == id && binding.MessageId == message.Id)).ToList();
            if (direct.Any(message => ReadableMessage(ResolveCanonical(database, message), conversation, visibility))) return true;
            Guid[] contents = database.MediaBindings.Where(binding => binding.MediaId == id && binding.ContentId != null).Select(binding => binding.ContentId!.Value).Distinct().ToArray();
            return contents.Any(content => ReadableContent(database, conversation, content, visibility));
        }, cancellationToken);
    }

    /// <summary>引用原消息撤回后不能通过已经展开的快照重新取得正文。</summary>
    private static bool ReadableContent(MissiliDbContext database, MessageAddress conversation, Guid id, MessageVisibilityPolicy visibility)
    {
        StructuredContentEntity? content = database.StructuredContents.AsNoTracking().SingleOrDefault(item => item.Id == id);
        if (content is null) return false;
        ContentSource source = JsonSerializer.Deserialize<ContentSource>(content.SourceJson)!;
        if (source.Address != conversation) return false;
        if (source.Kind == "reply")
        {
            MessageEntity? target = InConversation(database.Messages.AsNoTracking(), conversation).Where(message => message.PlatformMessageId == source.PlatformId && message.CanonicalId == null).OrderBy(message => message.Id).FirstOrDefault();
            if (target is not null) return ReadableMessage(target, conversation, visibility);
        }
        List<MessageEntity> owners = InConversation(database.Messages.AsNoTracking(), conversation)
            .Where(message => message.Segments.Any(segment => segment.ContentId == id)).ToList();
        return owners.Any(message => ReadableMessage(ResolveCanonical(database, message), conversation, visibility));
    }

    /// <summary>待发意图、失败与未知发送不能证明用户已在该会话看见归档内容。</summary>
    private static bool ReadableMessage(MessageEntity message, MessageAddress conversation, MessageVisibilityPolicy visibility)
        => Address(message) == conversation && (message.Direction == MessageDirection.Incoming || message.Delivery == DeliveryState.Confirmed) &&
            (visibility != MessageVisibilityPolicy.Placeholder || !message.Recalled && !message.Deleted);
}
