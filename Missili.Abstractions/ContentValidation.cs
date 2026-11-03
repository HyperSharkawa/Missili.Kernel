using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>共享的消息内容约束，避免发送和插件修改使用不同校验规则。</summary>
public static class ContentValidation
{
    /// <summary>检查段类型和互斥载荷，拒绝没有原件身份的媒体段。</summary>
    public static void Validate(ImmutableArray<MessageSegment> content)
    {
        if (content.IsDefaultOrEmpty) throw new ArgumentException("消息内容不能为空。");
        foreach (MessageSegment segment in content)
        {
            if (!Enum.IsDefined(segment.Kind)) throw new ArgumentException("不支持的消息段类型。");
            bool valid = segment.Kind == SegmentKind.Text
                ? segment.Text is not null && segment.MediaId is null
                : segment.Text is null && segment.MediaId.HasValue && segment.MediaId != Guid.Empty;
            if (!valid) throw new ArgumentException("消息段载荷与类型不一致。");
        }
    }
}
