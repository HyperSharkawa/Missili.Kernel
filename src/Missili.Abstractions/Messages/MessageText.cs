using System.Collections.Immutable;
using System.Text;

namespace Missili.Abstractions;

/// <summary>相邻真实文本段形成的连续正文；其他结构会打断片段，不能借此拼出伪命令或关键词。</summary>
public sealed record MessageTextFragment
{
    public required int StartSegment { get; init; }
    public required int SegmentCount { get; init; }
    public required string Text { get; init; }
}

/// <summary>宿主已经匹配的固定命令路径及剩余消息结构，不包含插件私有解析对象。</summary>
public sealed record CommandMatch
{
    public required ImmutableArray<string> Path { get; init; }
    public required ImmutableArray<MessageSegment> Arguments { get; init; }
}

/// <summary>命令与唤醒共用的字面正文投影，不读取卡片、引用、媒体或未知载荷内部内容。</summary>
public static class MessageText
{
    /// <summary>按原顺序连接相邻的已解析文本，不插入原消息没有的空格。</summary>
    public static ImmutableArray<MessageTextFragment> Fragments(ImmutableArray<MessageSegment> content)
    {
        if (content.IsDefault) throw new ArgumentException("消息段集合未初始化。");
        ImmutableArray<MessageTextFragment>.Builder fragments = ImmutableArray.CreateBuilder<MessageTextFragment>();
        for (int index = 0; index < content.Length; index++)
        {
            if (!IsText(content[index])) continue;
            int start = index;
            StringBuilder text = new();
            do { text.Append(content[index].Text); index++; } while (index < content.Length && IsText(content[index]));
            fragments.Add(new MessageTextFragment { StartSegment = start, SegmentCount = index - start, Text = text.ToString() });
            index--;
        }
        return fragments.ToImmutable();
    }

    /// <summary>从指定连续正文移除字符范围，保留段的结构、扩展字段和未匹配内容；允许留下空文本段。</summary>
    public static ImmutableArray<MessageSegment> Remove(ImmutableArray<MessageSegment> content, MessageTextFragment fragment, int start, int length)
    {
        if (start < 0 || length < 0 || start > fragment.Text.Length - length) throw new ArgumentOutOfRangeException(nameof(length));
        ImmutableArray<MessageSegment>.Builder result = content.ToBuilder();
        int offset = 0;
        for (int index = fragment.StartSegment; index < fragment.StartSegment + fragment.SegmentCount; index++)
        {
            MessageSegment segment = content[index];
            string text = segment.Text!;
            int from = Math.Max(start - offset, 0), until = Math.Min(start + length - offset, text.Length);
            if (from < until) result[index] = segment with { Text = text.Remove(from, until - from) };
            offset += text.Length;
        }
        return result.ToImmutable();
    }

    /// <summary>登记时校验路径的每个独立词，空白只由消息中的分隔符表达。</summary>
    public static void ValidateCommandPath(ImmutableArray<string> path)
    {
        if (path.IsDefaultOrEmpty || path.Any(word => string.IsNullOrWhiteSpace(word) || word.Any(char.IsWhiteSpace)))
            throw new ArgumentException("命令路径必须包含非空且不含空白的词。");
    }

    /// <summary>仅匹配首个真实文本片段，以词边界识别固定路径，返回已经移除路径的参数结构。</summary>
    public static CommandMatch? MatchCommand(ImmutableArray<string> path, ImmutableArray<MessageSegment> content)
    {
        ValidateCommandPath(path);
        MessageTextFragment? first = Fragments(content).FirstOrDefault();
        if (first is null) return null;
        string text = first.Text;
        int position = 0;
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        foreach (string word in path)
        {
            if (!text.AsSpan(position).StartsWith(word, StringComparison.Ordinal)) return null;
            position += word.Length;
            if (position < text.Length && !char.IsWhiteSpace(text[position])) return null;
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }
        return new CommandMatch { Path = path, Arguments = Remove(content, first, 0, position) };
    }

    /// <summary>只有已解析的真实文字参与投影，扩展段不能用 Text 字段伪装正文。</summary>
    private static bool IsText(MessageSegment segment) => segment.Kind == SegmentTypes.Text && segment.ParseState == SegmentParseState.Parsed;
}
