using System.Collections.Immutable;
using Fixture.PrivateDependency;
using Missili.Abstractions;

namespace Fixture.Plugin;

/// <summary>实际 DLL 中的处理器验证拆分、失败、过滤及发送重入。</summary>
internal sealed class FixtureProcessor : IOutgoingProcessor
{
    private readonly string _mode;
    private readonly IMessageSender _sender;

    /// <summary>固定夹具行为，执行过程仍走生产目录和发送服务。</summary>
    private FixtureProcessor(string mode, IMessageSender sender) { _mode = mode; _sender = sender; }

    /// <summary>同插件两个处理器可以顺序验证分片只继续向后传播。</summary>
    public static void Register(IPluginContext context, string mode)
    {
        context.OutgoingProcessors.Register(new OutgoingProcessorDefinition { Id = "transform", Name = "合成变换", Description = "验证出站流水线边界" }, new FixtureProcessor(mode, context.Sender));
        context.OutgoingProcessors.Register(new OutgoingProcessorDefinition { Id = "suffix", Name = "合成后缀", Description = "验证拆分后继续传播" }, new FixtureProcessor("suffix", context.Sender));
    }

    /// <summary>按测试模式产生共享内容，不在成功路径中自行发送。</summary>
    public async Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mode == "outgoing-fail") throw new InvalidOperationException("合成处理失败。");
        if (_mode == "outgoing-reenter") await _sender.SendAsync(context.Original, cancellationToken);
        if (_mode == "outgoing-filter") return new OutgoingProcessingResult { Messages = [], Filtered = true, Reason = "合成过滤" };
        ImmutableArray<MessageDraft>.Builder messages = ImmutableArray.CreateBuilder<MessageDraft>();
        foreach (MessageDraft message in context.Messages)
        {
            string text = string.Concat(message.Segments.Select(segment => segment.Text));
            if (_mode == "outgoing-split")
            {
                foreach (string part in text.Split('|')) messages.Add(new MessageDraft { Segments = [SegmentValues.Text(part)] });
            }
            else messages.Add(new MessageDraft { Segments = [SegmentValues.Text(text + ":" + VersionInfo.Read())] });
        }
        return new OutgoingProcessingResult { Messages = messages.ToImmutable() };
    }
}
