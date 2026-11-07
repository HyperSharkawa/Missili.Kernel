using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>模型侧历史服务固定会话与可见性，原生续接和审批诊断不进入普通记录读取。</summary>
internal sealed class ScopedToolHistory : IMessageHistory, IAgentHistory, IAgentToolHistory, IHistorySearch
{
    private readonly AgentToolContext _context;
    /// <summary>保存调用已经捕获的共享上下文，插件不能替换其真实授权范围。</summary>
    public ScopedToolHistory(AgentToolContext context) => _context = context;

    /// <summary>范围为空时使用本会话，显式指定其他会话的查询返回空页。</summary>
    public async Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.Address is not null && query.Address != _context.Conversation) return new HistoryPage { Messages = [] };
        HistoryPage page = await _context.History.QueryAsync(query with { Address = _context.Conversation }, cancellationToken).ConfigureAwait(false);
        return page with { Messages = page.Messages.Select(Visible).ToImmutableArray() };
    }

    /// <summary>已知消息标识不能跨会话读取，隐藏正文时连同原始元数据一起移除。</summary>
    public async Task<MessageSnapshot?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        MessageSnapshot? message = await _context.History.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return message?.Address == _context.Conversation ? Visible(message) : null;
    }

    /// <summary>先核对锚点归属，再读取同会话邻近记录。</summary>
    public async Task<ImmutableArray<MessageSnapshot>> GetAroundAsync(long id, int before, int after, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is null) return [];
        return (await _context.History.GetAroundAsync(id, before, after, cancellationToken).ConfigureAwait(false)).Where(message => message.Address == _context.Conversation).Select(Visible).ToImmutableArray();
    }

    /// <summary>任务状态保留预算与身份，系统指令和管理员名单不成为可检索业务资料。</summary>
    public async Task<AgentTaskSnapshot?> GetTaskAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default)
    {
        if (conversation != _context.Conversation) return null;
        AgentTaskSnapshot? task = await _context.AgentHistory.GetTaskAsync(conversation, taskId, cancellationToken).ConfigureAwait(false);
        return task is null ? null : task with { InitialProfile = task.InitialProfile with { Instructions = "", Administrators = [], Templates = task.InitialProfile.Templates.Select(template => template with { Instructions = "" }).ToImmutableArray() } };
    }

    /// <summary>根任务要求通过受可见性约束的输入记录读取，避免身份摘要绕过撤回策略。</summary>
    public async Task<ImmutableArray<AgentSnapshot>> GetAgentsAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default)
    {
        if (conversation != _context.Conversation) return [];
        ImmutableArray<AgentSnapshot> agents = await _context.AgentHistory.GetAgentsAsync(conversation, taskId, cancellationToken).ConfigureAwait(false);
        return agents.Select(agent => agent with
        {
            Assignment = agent.ParentId is null ? null : agent.Assignment,
            Result = agent.Result is null ? null : agent.Result with { Content = agent.Result.Content.Where(block => block.Kind != ModelBlockKind.Native).ToImmutableArray() }
        }).ToImmutableArray();
    }

    /// <summary>搜索返回的条目身份仍通过相同的范围与可见性路径读取。</summary>
    public async Task<AgentEntry?> GetEntryAsync(MessageAddress conversation, Guid entryId, CancellationToken cancellationToken = default)
    {
        if (conversation != _context.Conversation) return null;
        AgentEntry? entry = await _context.AgentHistory.GetEntryAsync(conversation, entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return null;
        AgentEntryPage page = await ReadEntriesAsync(conversation, entry.TaskId, entry.AgentId, entry.Sequence - 1, 1, cancellationToken).ConfigureAwait(false);
        return page.Entries.SingleOrDefault(item => item.Id == entryId);
    }

    /// <summary>普通记录保留完整标准工具数据，续接签名和原生推理只留在诊断存储。</summary>
    public async Task<AgentEntryPage> ReadEntriesAsync(MessageAddress conversation, Guid taskId, Guid agentId, long afterSequence, int pageSize, CancellationToken cancellationToken = default)
    {
        if (conversation != _context.Conversation) return new AgentEntryPage { Entries = [] };
        AgentEntryPage page = await _context.AgentHistory.ReadEntriesAsync(conversation, taskId, agentId, afterSequence, pageSize, cancellationToken).ConfigureAwait(false);
        ImmutableArray<AgentEntry>.Builder entries = ImmutableArray.CreateBuilder<AgentEntry>();
        foreach (AgentEntry entry in page.Entries)
        {
            JsonElement data = entry.Data;
            if (entry.Kind == AgentEntryKind.ModelOutput)
            {
                ModelResult model = JsonSerializer.Deserialize<ModelResult>(data)!;
                data = JsonSerializer.SerializeToElement(model with { Content = model.Content.Where(block => block.Kind != ModelBlockKind.Native).ToImmutableArray(), Continuation = null, Attempts = [] });
            }
            else if (entry.Kind == AgentEntryKind.WakeInput && entry.SourceMessageId is long sourceId)
            {
                MessageSnapshot? source = await _context.History.GetAsync(sourceId, cancellationToken).ConfigureAwait(false);
                AgentWakeInput input = JsonSerializer.Deserialize<AgentWakeInput>(data)!;
                if (source is null || source.Recalled || source.Deleted)
                {
                    if (source is null || _context.Profile.Profile.Visibility == MessageVisibilityPolicy.Placeholder) input = input with
                    { Content = [SegmentValues.Text("[原用户消息已撤回、删除或不可用]")], Cause = input.Cause with { MatchedText = null } };
                    else if (_context.Profile.Profile.Visibility == MessageVisibilityPolicy.MarkedOriginal) input = input with { Content = input.Content.Insert(0, SegmentValues.Text("[原用户消息已撤回或删除]")) };
                }
                data = JsonSerializer.SerializeToElement(input);
            }
            else if (entry.Kind == AgentEntryKind.Result)
            {
                AgentResult result = JsonSerializer.Deserialize<AgentResult>(data)!;
                data = JsonSerializer.SerializeToElement(result with { Content = result.Content.Where(block => block.Kind != ModelBlockKind.Native).ToImmutableArray() });
            }
            entries.Add(entry with { Data = data });
        }
        return page with { Entries = entries.ToImmutable() };
    }

    /// <summary>工具原参数与业务输出可以展开，审批政策及其完整证据属于宿主诊断边界。</summary>
    public async Task<AgentToolRecord?> GetToolCallAsync(MessageAddress conversation, Guid id, CancellationToken cancellationToken = default)
    {
        if (conversation != _context.Conversation) return null;
        AgentToolRecord? record = await _context.ToolHistory.GetToolCallAsync(conversation, id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : record with { ApprovalRequest = null, DiagnosticFieldsOmitted = true };
    }

    /// <summary>检索策略由宿主配置固定，工具不能通过参数要求忽略撤回。</summary>
    public Task<HistorySearchPage> SearchAsync(HistorySearchQuery query, CancellationToken cancellationToken = default)
        => query.Conversation != _context.Conversation ? Task.FromResult(new HistorySearchPage { Hits = [] })
            : _context.Search.SearchAsync(query with { Visibility = _context.Profile.Profile.Visibility }, cancellationToken);

    /// <summary>撤回占位清除所有正文载体，标记原文模式保留原段及显式提示。</summary>
    private MessageSnapshot Visible(MessageSnapshot message)
    {
        if (!message.Recalled && !message.Deleted || _context.Profile.Profile.Visibility == MessageVisibilityPolicy.Original) return message;
        return _context.Profile.Profile.Visibility == MessageVisibilityPolicy.Placeholder
            ? message with { Segments = [SegmentValues.Text("[此消息已撤回或删除]")], Metadata = null }
            : message with { Segments = message.Segments.Insert(0, SegmentValues.Text("[此消息已撤回或删除]")) };
    }
}
