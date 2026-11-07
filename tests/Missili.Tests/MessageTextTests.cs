using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>命令与唤醒共用的文本边界，保留真实分片及非文本结构。</summary>
public sealed class MessageTextTests
{
    /// <summary>命令可以跨相邻文本段，参数保留结构；较长词和不完整路径不会误命中。</summary>
    [Fact]
    public void CommandPathMatchesTextFragmentsAndWordBoundaries()
    {
        ImmutableArray<MessageSegment> content = [SegmentValues.Text("  /ag"), SegmentValues.Text("ent\tst"), SegmentValues.Text("op  target"), SegmentValues.Mention("person")];
        CommandMatch match = Assert.IsType<CommandMatch>(MessageText.MatchCommand(["/agent", "stop"], content));
        Assert.Equal("target", string.Concat(MessageText.Fragments(match.Arguments).Select(fragment => fragment.Text)));
        Assert.Equal(content[3], match.Arguments[3]);
        Assert.Equal("  /agent\tstop  target", string.Concat(MessageText.Fragments(content).Select(fragment => fragment.Text)));
        Assert.Null(MessageText.MatchCommand(["/agent", "stop"], [SegmentValues.Text("/agent stopx")]));
        Assert.Null(MessageText.MatchCommand(["/agent", "stop"], [SegmentValues.Text("/agent")]));
        Assert.Null(MessageText.MatchCommand(["/agent", "stop"], [SegmentValues.Text("/agentstop")]));
    }

    /// <summary>引用、提及和卡片不贡献正文，也不能连接两侧文字形成命令或关键词。</summary>
    [Fact]
    public void NonTextBreaksFragmentsAndPrefixRemovalKeepsOriginalStructure()
    {
        MessageSegment card = new() { Kind = SegmentTypes.Json, Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = "/agent stop" }) };
        ImmutableArray<MessageSegment> content = [SegmentValues.Text(" /ag"), card, SegmentValues.Text("ent stop")];
        Assert.Equal(new[] { " /ag", "ent stop" }, MessageText.Fragments(content).Select(fragment => fragment.Text));
        Assert.Null(MessageText.MatchCommand(["/agent", "stop"], content));
        ImmutableArray<MessageSegment> original = [SegmentValues.Text("  /"), SegmentValues.Text("agent你好"), card];
        MessageTextFragment first = MessageText.Fragments(original)[0];
        ImmutableArray<MessageSegment> removed = MessageText.Remove(original, first, 2, 6);
        Assert.Equal("  你好", MessageText.Fragments(removed)[0].Text);
        Assert.Equal("  /agent你好", MessageText.Fragments(original)[0].Text);
        Assert.Equal(card, removed[2]);
    }
}
