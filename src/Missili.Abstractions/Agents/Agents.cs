using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>任务结局与实际资源退出分开，停止请求不会提前释放会话归属。</summary>
public enum AgentTaskState { Active, Stopping, Completed, Cancelled, Failed, Interrupted }

/// <summary>代理当前活动仅用于调度和诊断，不替代 Nexus 的插件状态。</summary>
public enum AgentActivity { Ready, RunningModel, RunningTool, Waiting, Idle, Stopping, Closed }

/// <summary>唤醒原因只解释为何安排决策，不代表用户授权；Unknown 仅用于迁移后的旧记录。</summary>
public enum AgentWakeKind { Unknown, Prefix, Mention, Keyword, Probability }

/// <summary>固定本条消息使用的规则与随机采样，配置更新不会重算已经登记的唤醒。</summary>
public sealed record AgentWakeCause
{
    public required AgentWakeKind Kind { get; init; }
    public long ConfigurationRevision { get; init; }
    public string? MatchedText { get; init; }
    public double? Probability { get; init; }
    public double? Sample { get; init; }

    /// <summary>新唤醒必须提供真实规则种类，旧记录的未知来源不能用于新请求。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Kind == AgentWakeKind.Unknown || ConfigurationRevision < 0)
            throw new ArgumentException("唤醒原因或配置修订无效。");
        bool text = Kind is AgentWakeKind.Prefix or AgentWakeKind.Keyword;
        if (text ? string.IsNullOrWhiteSpace(MatchedText) : MatchedText is not null)
            throw new ArgumentException("唤醒匹配文本与规则种类不一致。");
        if (Kind == AgentWakeKind.Probability)
        {
            if (Probability is not double probability || Sample is not double sample || !double.IsFinite(probability) || !double.IsFinite(sample) ||
                probability is <= 0 or > 1 || sample < 0 || sample >= probability)
                throw new ArgumentException("概率唤醒必须记录有效的命中采样。");
        }
        else if (Probability is not null || Sample is not null) throw new ArgumentException("非概率唤醒不能携带采样。");
    }
}

/// <summary>停止身份只引用真实用户消息，不接受自由填写的管理员或发起者字段。</summary>
public sealed record AgentStopRequest
{
    public required long MessageId { get; init; }
    public Guid? AgentId { get; init; }
    public bool Emergency { get; init; }
}

/// <summary>控制结果只承诺请求已受理，真实工作退出由状态查询单独表达。</summary>
public sealed record AgentControlResult
{
    public bool Accepted { get; init; }
    public Guid? TaskId { get; init; }
    public string? Error { get; init; }
}

/// <summary>公开活动查询同时提供持久化状态与实际工作数量，终态不伪装为资源已经退出。</summary>
public sealed record AgentTaskStatus
{
    public required AgentTaskSnapshot Task { get; init; }
    public ImmutableArray<AgentSnapshot> Agents { get; init; } = [];
    public int ActiveWork { get; init; }
    public int PendingInputs { get; init; }
    public int WaitingAgents { get; init; }
}

/// <summary>健康检查仅暴露运行数量，不包含会话身份、正文或配置。</summary>
public sealed record AgentRuntimeCounts
{
    public int ActiveTasks { get; init; }
    public int WorkingAgents { get; init; }
    public int WaitingAgents { get; init; }
    public int PendingWakeAdmissions { get; init; }
}

/// <summary>薄入口只登记消息唤醒与查询宿主运行，已登记唤醒工作不继承入口插件的生命期。</summary>
public interface IAgentService
{
    /// <summary>完成身份核验、容量预留和持久登记后立即返回，不等待模型或工具完成。</summary>
    Task<AgentWakeResult> WakeAsync(AgentWakeRequest request, CancellationToken cancellationToken = default);
    /// <summary>由当前明确配置的管理员请求当前树或指定子树停止。</summary>
    Task<AgentControlResult> RequestStopAsync(AgentStopRequest request, CancellationToken cancellationToken = default);
    /// <summary>查询当前会话未释放的主任务及真实活动情况。</summary>
    Task<AgentTaskStatus?> GetActiveAsync(MessageAddress conversation, CancellationToken cancellationToken = default);
}

/// <summary>登记唤醒结果明确区分去重、容量和未就绪，不因聊天已经保存而声称任务已接受。</summary>
public enum AgentWakeState { Created, Queued, Duplicate, Busy, Disabled, NotReady, CapacityExceeded, Rejected }

/// <summary>有序记录采用宿主稳定种类，正文保存共享 JSON 而不是插件私有类型。</summary>
public enum AgentEntryKind { WakeInput = 0, ModelOutput, ToolResults, AgentMessage, Followup, Result, Control, Wait }

/// <summary>入口只传原消息身份和处理后的内容，发言者与会话由宿主核对。</summary>
public sealed record AgentWakeRequest
{
    public required long MessageId { get; init; }
    public required ImmutableArray<MessageSegment> CurrentContent { get; init; }
    public required AgentWakeCause Cause { get; init; }
}

/// <summary>登记唤醒成功返回已保存的稳定身份，重复提交定位原记录。</summary>
public sealed record AgentWakeResult
{
    public required AgentWakeState State { get; init; }
    public Guid? TaskId { get; init; }
    public Guid? AgentId { get; init; }
    public long? EntrySequence { get; init; }
    public string? Error { get; init; }
    public bool Registered => State is AgentWakeState.Created or AgentWakeState.Queued or AgentWakeState.Duplicate;
}

/// <summary>每次输入保存自己的真实发言者和原消息引用，处理后的内容不覆盖聊天原件。</summary>
public sealed record AgentWakeInput
{
    public required long MessageId { get; init; }
    public required string SenderId { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public required ImmutableArray<MessageSegment> Content { get; init; }
    public required AgentWakeCause Cause { get; init; }
}

/// <summary>框架统计实际已预留次数，不依赖模型自报用量或费用。</summary>
public sealed record AgentUsage
{
    public int ModelCalls { get; init; }
    public int ToolCalls { get; init; }
    public int TotalAgents { get; init; }
}

/// <summary>可查询的根任务；初始权限与预算用于限制后续热更新和委派。</summary>
public sealed record AgentTaskSnapshot
{
    public required Guid Id { get; init; }
    public required Guid RootAgentId { get; init; }
    public required Guid RunId { get; init; }
    public required MessageAddress Conversation { get; init; }
    public required string TriggerSenderId { get; init; }
    public required string ProfileId { get; init; }
    public long ConfigurationRevision { get; init; }
    public required AgentProfile InitialProfile { get; init; }
    public required AgentTaskState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public string? Error { get; init; }
    public AgentUsage Usage { get; init; } = new();
}

/// <summary>代理成果只包含可展示内容和实际结果引用，原生推理另存于模型轮次。</summary>
public sealed record AgentResult
{
    public ImmutableArray<ModelBlock> Content { get; init; } = [];
    public ImmutableArray<Guid> ToolInvocations { get; init; } = [];
    public bool Complete { get; init; }
    public string? Error { get; init; }
    public string? Unfinished { get; init; }
}

/// <summary>一个独立代理的身份和活动；自然完成一轮进入空闲，仍可接收补充工作。</summary>
public sealed record AgentSnapshot
{
    public required Guid Id { get; init; }
    public required Guid TaskId { get; init; }
    public Guid? ParentId { get; init; }
    public int Depth { get; init; }
    public string? TemplateId { get; init; }
    public string? Assignment { get; init; }
    public required AgentActivity Activity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StopRequestedAt { get; init; }
    public DateTimeOffset? StopDeadline { get; init; }
    public DateTimeOffset? ExitedAt { get; init; }
    public int DecisionCalls { get; init; }
    public long ResultRevision { get; init; }
    public AgentResult? Result { get; init; }
}

/// <summary>记录是审计事实而非可重放任务，进程重启不会再次执行其中的工具。</summary>
public sealed record AgentEntry
{
    public required Guid Id { get; init; }
    public required Guid TaskId { get; init; }
    public required Guid AgentId { get; init; }
    public required long Sequence { get; init; }
    public required AgentEntryKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public long? SourceMessageId { get; init; }
    public required JsonElement Data { get; init; }
}

/// <summary>任务记录使用有限游标分页，不把整个任务轨迹一次装入内存。</summary>
public sealed record AgentEntryPage
{
    public required ImmutableArray<AgentEntry> Entries { get; init; }
    public long? NextSequence { get; init; }
}

/// <summary>宿主读取任务与成果时必须给出允许的完整会话身份。</summary>
public interface IAgentHistory
{
    /// <summary>按完整会话范围读取搜索或工具返回的条目身份，不执行其中的历史动作。</summary>
    Task<AgentEntry?> GetEntryAsync(MessageAddress conversation, Guid entryId, CancellationToken cancellationToken = default);
    /// <summary>按会话范围读取任务，越界和不存在均不泄露记录。</summary>
    Task<AgentTaskSnapshot?> GetTaskAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default);
    /// <summary>读取同任务的代理目录及最新成果。</summary>
    Task<ImmutableArray<AgentSnapshot>> GetAgentsAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default);
    /// <summary>分页读取一个代理的记录，正文可见性由上层模型投影再次应用。</summary>
    Task<AgentEntryPage> ReadEntriesAsync(MessageAddress conversation, Guid taskId, Guid agentId, long afterSequence, int pageSize, CancellationToken cancellationToken = default);
}
