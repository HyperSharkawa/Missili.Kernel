using Missili.Abstractions;

namespace Missili.Samples.Keyword;

/// <summary>示例插件：只读取文字，媒体未完成时也可以匹配关键词。</summary>
public sealed class Entry : IPlugin
{
    private IPluginContext? _context;
    private string _keyword = "";
    private string _reply = "";

    /// <summary>读取必填配置并注册普通监听器，示例不会自动接管消息。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keyword = context.Configuration.GetProperty("keyword").GetString() ?? throw new ArgumentException("keyword 必须是字符串。");
        _reply = context.Configuration.GetProperty("reply").GetString() ?? throw new ArgumentException("reply 必须是字符串。");
        ArgumentException.ThrowIfNullOrWhiteSpace(_keyword);
        ArgumentException.ThrowIfNullOrWhiteSpace(_reply);
        _context = context;
        context.Handlers.RegisterListener(MessageHooks.Received, "reply", "关键词回复", "文字匹配后使用宿主发送服务回复", 0, ReplyAsync);
        return Task.CompletedTask;
    }

    /// <summary>示例没有自有后台资源，在途调用由宿主通过令牌统一取消。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>精确匹配任一文本段；不访问或等待同条消息中的媒体。</summary>
    private async Task ReplyAsync(MessageContext message, CancellationToken cancellationToken)
    {
        if (!message.CurrentContent.Any(segment => segment.Kind == SegmentTypes.Text && segment.ParseState == SegmentParseState.Parsed && string.Equals(segment.Text, _keyword, StringComparison.Ordinal))) return;
        await _context!.Sender.SendAsync(new OutgoingMessage
        {
            Address = message.OriginalMessage.Address,
            Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = _reply }]
        }, cancellationToken);
    }
}
