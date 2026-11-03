using Missili.Abstractions;

namespace Missili.Samples.History;

/// <summary>示例插件：通过稳定查询接口读取当前会话的有限历史。</summary>
public sealed class Entry : IPlugin
{
    private IPluginContext? _context;

    /// <summary>注册通用文本命令，不依赖 QQ 消息字段。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        context.Handlers.RegisterCommand("recent", "最近历史", "查询当前会话最近十条可见记录", "/history", 0, QueryAsync);
        return Task.CompletedTask;
    }

    /// <summary>示例不持有数据库上下文或后台任务。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>显式选择查询可见性和时间依据，结果只输出记录数量。</summary>
    private async Task QueryAsync(MessageContext message, CancellationToken cancellationToken)
    {
        HistoryPage page = await _context!.History.QueryAsync(new HistoryQuery
        {
            Address = message.OriginalMessage.Address,
            Time = HistoryTime.Received,
            PageDirection = PageDirection.Backward,
            IncludeDeleted = false,
            IncludeRecalled = false,
            IncludeUnconfirmed = false,
            PageSize = 10
        }, cancellationToken);
        await _context.Sender.SendAsync(new OutgoingMessage
        {
            Address = message.OriginalMessage.Address,
            Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = $"读取到 {page.Messages.Length} 条会话历史。" }]
        }, cancellationToken);
    }
}
