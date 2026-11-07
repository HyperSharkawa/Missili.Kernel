using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>共享的消息内容约束，避免发送和插件修改使用不同校验规则。</summary>
public static class ContentValidation
{
    /// <summary>验证出站段的引用与不可变载荷，拒绝未初始化集合。</summary>
    public static void Validate(ImmutableArray<MessageSegment> content)
    {
        if (content.IsDefaultOrEmpty) throw new ArgumentException("消息内容不能为空。");
        foreach (MessageSegment segment in content)
        {
            ValidateSegment(segment.Kind, segment.Version, segment.Text, segment.Payload, segment.ParseState);
            if (segment.MediaIds.IsDefault || segment.MediaIds.Any(id => id == Guid.Empty) || segment.MediaIds.Distinct().Count() != segment.MediaIds.Length)
                throw new ArgumentException("媒体引用集合无效。");
        }
    }

    /// <summary>文本与结构载荷分开；未知或错误段仍能保存原始载荷。</summary>
    public static void ValidateSegment(string kind, int version, string? text, JsonElement? payload, SegmentParseState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (!Enum.IsDefined(state)) throw new ArgumentException("解析状态无效。");
        if (payload?.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("JSON 载荷未初始化。");
        if (kind == SegmentTypes.Text && state == SegmentParseState.Parsed)
        {
            if (text is null || (payload.HasValue && (payload.Value.ValueKind != JsonValueKind.Object || payload.Value.TryGetProperty("text", out _))))
                throw new ArgumentException("普通文本必须提供正文，扩展 JSON 不能重复保存正文。");
        }
        else if (text is not null) throw new ArgumentException("非普通文本段不能伪装为文本正文。");
    }
}
