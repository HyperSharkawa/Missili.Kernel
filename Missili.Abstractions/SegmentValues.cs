using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>常用段的强类型入口，存储仍使用稳定类型和 JSON。</summary>
public static class SegmentValues
{
    /// <summary>构造真实文本，不解析其中的 CQ 字符串。</summary>
    public static MessageSegment Text(string text) => new() { Kind = SegmentTypes.Text, Text = text };
    /// <summary>构造指定用户或全体成员的提及。</summary>
    public static MessageSegment Mention(string userId) => Data(SegmentTypes.Mention, "userId", userId);
    /// <summary>构造平台消息引用，作用域由发送地址提供。</summary>
    public static MessageSegment Reply(string messageId) => Data(SegmentTypes.Reply, "messageId", messageId);
    /// <summary>构造 QQ 表情，保留平台标识。</summary>
    public static MessageSegment Face(string id) => Data(SegmentTypes.Face, "id", id);
    /// <summary>构造已归档原件的媒体段。</summary>
    public static MessageSegment Media(string kind, params Guid[] ids) => new() { Kind = kind, MediaIds = ids.ToImmutableArray() };
    /// <summary>构造具有明确标题和地址的分享。</summary>
    public static MessageSegment Share(string url, string title) => new()
    { Kind = SegmentTypes.Share, Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["url"] = url, ["title"] = title }) };
    /// <summary>构造标准发送允许的自定义转发节点集合，不把接收的转发资源标识直接发送。</summary>
    public static MessageSegment Forward(ImmutableArray<ForwardNode> nodes) => new()
    {
        Kind = SegmentTypes.Forward,
        Payload = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["nodes"] = nodes.Select(node => node.MessageId is not null
            ? new Dictionary<string, object> { ["messageId"] = node.MessageId }
            : new Dictionary<string, object> { ["userId"] = node.UserId!, ["nickname"] = node.Nickname!, ["segments"] = node.Segments }).ToArray()
        })
    };
    /// <summary>读取必填字符串，类型错误不能静默转换。</summary>
    public static string RequiredString(this MessageSegment segment, string field) => segment.Payload?.GetProperty(field).GetString()
        ?? throw new InvalidDataException("消息段缺少字符串字段：" + field);
    /// <summary>读取提及目标，类型不匹配时拒绝。</summary>
    public static string MentionTarget(this MessageSegment segment) => segment.Kind == SegmentTypes.Mention ? segment.RequiredString("userId") : throw new ArgumentException("不是提及段。");
    /// <summary>读取引用的目标消息标识。</summary>
    public static string ReplyTarget(this MessageSegment segment) => segment.Kind == SegmentTypes.Reply ? segment.RequiredString("messageId") : throw new ArgumentException("不是引用段。");
    /// <summary>构造有必填字符串的段，仅序列化框架共享字典。</summary>
    private static MessageSegment Data(string kind, string field, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new MessageSegment { Kind = kind, Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [field] = value }) };
    }
}

/// <summary>发送转发节点可以引用平台消息，也可以显式指定作者与内容。</summary>
public sealed record ForwardNode
{
    public string? MessageId { get; init; }
    public string? UserId { get; init; }
    public string? Nickname { get; init; }
    public ImmutableArray<MessageSegment> Segments { get; init; } = [];
}
