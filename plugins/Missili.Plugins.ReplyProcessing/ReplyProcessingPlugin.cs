using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Missili.Abstractions;

namespace Missili.Plugins.ReplyProcessing;

/// <summary>官方后处理只变换共享草稿，生成模型和实际发送均由其他组件负责。</summary>
public sealed class ReplyProcessingPlugin : IPlugin, IOutgoingProcessor, IPluginConfigurationValidator
{
    /// <summary>两种能力一起登记，配置未经校验时不会发布处理器。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ConfigurationValidators.Register(this);
        context.OutgoingProcessors.Register(new OutgoingProcessorDefinition { Id = "process", Name = "回复后处理", Description = "按顺序执行正则替换和 Unicode 自然分段" }, this);
        return Task.CompletedTask;
    }

    /// <summary>插件没有后台任务或持久执行资源。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>实际解析规则和正则语法，所有错误作为共享描述返回。</summary>
    public Task<ImmutableArray<PluginConfigurationError>> ValidateAsync(PluginConfigurationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = ProcessingSettings.Parse(context.Configuration); return Task.FromResult(ImmutableArray<PluginConfigurationError>.Empty); }
        catch (ArgumentException exception) { return Task.FromResult(ImmutableArray.Create(new PluginConfigurationError { Path = "$", Message = exception.Message })); }
    }

    /// <summary>来源筛选在变换前执行，所有步骤严格使用本批冻结的配置。</summary>
    public Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken)
    {
        ProcessingSettings settings = ProcessingSettings.Parse(context.Configuration);
        if (!settings.Purposes.Contains(context.Original.Purpose, StringComparer.Ordinal) ||
            !settings.SourcePlugins.IsEmpty && !settings.SourcePlugins.Contains(context.Original.Origin.PluginId ?? "", StringComparer.Ordinal) ||
            !settings.SourceTools.IsEmpty && !settings.SourceTools.Contains(context.Original.SourceToolId ?? "", StringComparer.Ordinal))
            return Task.FromResult(new OutgoingProcessingResult { Messages = context.Messages });
        ImmutableArray<MessageDraft> messages = context.Messages;
        foreach (ProcessingStep step in settings.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            messages = step.Pattern is null ? messages.SelectMany(message => Split(message, step.MaximumTextElements, cancellationToken)).ToImmutableArray()
                : Replace(messages, step.Pattern, step.Replacement!, cancellationToken);
        }
        return Task.FromResult(new OutgoingProcessingResult { Messages = messages, Filtered = messages.IsEmpty, Reason = messages.IsEmpty ? "后处理规则移除了全部内容。" : null });
    }

    /// <summary>仅替换真实文本段，媒体、提及、引用以及扩展段保持原位。</summary>
    private static ImmutableArray<MessageDraft> Replace(ImmutableArray<MessageDraft> messages, Regex pattern, string replacement, CancellationToken token)
    {
        ImmutableArray<MessageDraft>.Builder result = ImmutableArray.CreateBuilder<MessageDraft>();
        foreach (MessageDraft message in messages)
        {
            ImmutableArray<MessageSegment>.Builder segments = ImmutableArray.CreateBuilder<MessageSegment>();
            foreach (MessageSegment segment in message.Segments)
            {
                token.ThrowIfCancellationRequested();
                if (segment.Kind != SegmentTypes.Text || segment.ParseState != SegmentParseState.Parsed) { segments.Add(segment); continue; }
                string changed = pattern.Replace(segment.Text!, replacement);
                if (changed.Length > 0) segments.Add(segment with { Text = changed });
            }
            if (segments.Count > 0) result.Add(message with { Segments = segments.ToImmutable() });
        }
        return result.ToImmutable();
    }

    /// <summary>按文本元素累计长度，非文本结构只出现一次，任何分割都保留原始字符。</summary>
    private static ImmutableArray<MessageDraft> Split(MessageDraft message, int maximum, CancellationToken token)
    {
        ImmutableArray<MessageDraft>.Builder result = ImmutableArray.CreateBuilder<MessageDraft>();
        ImmutableArray<MessageSegment>.Builder segments = ImmutableArray.CreateBuilder<MessageSegment>();
        int used = 0;
        foreach (MessageSegment segment in message.Segments)
        {
            token.ThrowIfCancellationRequested();
            if (segment.Kind != SegmentTypes.Text || segment.ParseState != SegmentParseState.Parsed) { segments.Add(segment); continue; }
            string text = segment.Text!;
            int[] starts = StringInfo.ParseCombiningCharacters(text);
            int position = 0;
            while (position < starts.Length)
            {
                token.ThrowIfCancellationRequested();
                if (used == maximum) { result.Add(message with { Segments = segments.ToImmutable() }); segments.Clear(); used = 0; }
                int count = Math.Min(maximum - used, starts.Length - position);
                if (position + count < starts.Length) count = Boundary(text, starts, position, count);
                int end = position + count < starts.Length ? starts[position + count] : text.Length;
                segments.Add(segment with { Text = text[starts[position]..end] });
                position += count; used += count;
                if (position < starts.Length) { result.Add(message with { Segments = segments.ToImmutable() }); segments.Clear(); used = 0; }
            }
        }
        if (segments.Count > 0) result.Add(message with { Segments = segments.ToImmutable() });
        return result.ToImmutable();
    }

    /// <summary>限额内优先采用段落、换行、句末；没有自然边界才使用完整文本元素边界。</summary>
    private static int Boundary(string text, int[] starts, int offset, int count)
    {
        int paragraph = 0, newline = 0, sentence = 0;
        for (int index = 0; index < count; index++)
        {
            int start = starts[offset + index];
            int end = offset + index + 1 < starts.Length ? starts[offset + index + 1] : text.Length;
            ReadOnlySpan<char> element = text.AsSpan(start, end - start);
            if (element.Contains('\n'))
            {
                newline = index + 1;
                if (start > 0 && (text[start - 1] == '\n' || start > 1 && text[start - 1] == '\r' && text[start - 2] == '\n')) paragraph = index + 1;
            }
            else if (element.Length == 1 && "。！？.!?；;".Contains(element[0], StringComparison.Ordinal)) sentence = index + 1;
        }
        return paragraph > 0 ? paragraph : newline > 0 ? newline : sentence > 0 ? sentence : count;
    }
}
