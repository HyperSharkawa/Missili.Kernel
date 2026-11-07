using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>存储登记唤醒参数由宿主构造，插件和模型不能自行填写真实发言者身份。</summary>
public sealed record AgentWakeAdmission
{
    public required AgentWakeRequest WakeRequest { get; init; }
    public required ConversationProfileSnapshot Profile { get; init; }
    public required Guid RunId { get; init; }
    public required Guid NewTaskId { get; init; }
    public required Guid NewRootAgentId { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>Agent 持久化边界全部使用短事务；网络、等待和插件代码不进入写执行器。</summary>
public interface IAgentStore : IAgentHistory
{
    /// <summary>读取已登记唤醒的原消息身份，重复输入不再消耗任务或邮箱容量。</summary>
    Task<AgentWakeResult?> FindWakeAsync(long messageId, CancellationToken cancellationToken);
    /// <summary>原子核对消息、去重及会话占有，并保存创建或追加输入。</summary>
    Task<AgentWakeResult> RegisterWakeAsync(AgentWakeAdmission request, CancellationToken cancellationToken);
    /// <summary>读取尚未释放主代理归属的任务，包括正在停止和等待资源退出的任务。</summary>
    Task<AgentTaskSnapshot?> GetActiveTaskAsync(MessageAddress conversation, CancellationToken cancellationToken);
    /// <summary>追加共享记录并分配单代理有序序号，返回已经提交的事实。</summary>
    Task<AgentEntry> AppendEntryAsync(Guid agentId, AgentEntryKind kind, JsonElement data, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>成果通知保存稳定引用并幂等分配接收者序号，不复制完整成果。</summary>
    Task<AgentEntry> AppendResultNotificationAsync(Guid agentId, Guid resultAgentId, long revision, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>实际工作切换时保存活动状态，不隐式释放会话归属。</summary>
    Task SetActivityAsync(Guid agentId, AgentActivity activity, CancellationToken cancellationToken);
    /// <summary>保存本轮成果并递增修订，通知只能在该操作成功后发出。</summary>
    Task<AgentSnapshot> CompleteTurnAsync(Guid agentId, AgentResult result, DateTimeOffset now, bool closed, CancellationToken cancellationToken);
    /// <summary>预留模型或工具调用预算，失败时不能开始对应外部动作。</summary>
    Task<bool> ReserveCallsAsync(Guid agentId, int modelCalls, int toolCalls, bool decision, bool settling, CancellationToken cancellationToken);
    /// <summary>原子检查累计数量与深度后创建子代理，资料和具体任务由运行器事先冻结。</summary>
    Task<AgentSnapshot?> CreateChildAsync(Guid parentId, string templateId, string task, JsonElement input, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>保存停止请求和共享期限，重复请求只能保留或提前现有期限。</summary>
    Task RequestStopAsync(Guid taskId, Guid targetAgentId, ImmutableArray<Guid> agents, DateTimeOffset now, DateTimeOffset deadline, CancellationToken cancellationToken);
    /// <summary>实际资源退出后才允许释放任务的会话归属，不因此重放任何工作。</summary>
    Task FinishTaskAsync(Guid taskId, AgentTaskState state, string? error, DateTimeOffset now, bool release, CancellationToken cancellationToken);
}
