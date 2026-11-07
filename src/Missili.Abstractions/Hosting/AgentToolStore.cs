using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>工具组绑定已经保存的完整模型条目，预算拒绝也不丢弃原始输出。</summary>
public sealed record AgentToolGroup
{
    public required Guid AgentId { get; init; }
    public required Guid TurnId { get; init; }
    public required Guid ModelEntryId { get; init; }
    public required ModelResult Model { get; init; }
    public required ImmutableArray<AgentToolIntent> Calls { get; init; }
}

/// <summary>重复组返回已有记录；只有首次成功预留的调用者有资格执行。</summary>
public sealed record AgentToolReservation
{
    public bool Created { get; init; }
    public ImmutableArray<AgentToolRecord> Calls { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>工具记录写入仅由宿主执行协调器持有，插件取得独立的只读接口。</summary>
public interface IAgentToolStore : IAgentToolHistory
{
    /// <summary>核对完整模型条目后原子保存调用组和预算，重复调用不重复扣减或执行。</summary>
    Task<AgentToolReservation> BeginToolGroupAsync(AgentToolGroup group, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>冻结无副作用准备结果，尚未批准时不允许进入执行。</summary>
    Task PrepareToolAsync(Guid id, PreparedToolCall prepared, CancellationToken cancellationToken);
    /// <summary>保存本次具体动作的审批依据及结论，写入失败必须零执行。</summary>
    Task ApproveToolAsync(Guid id, ToolApprovalRequest request, ToolApprovalResult approval, CancellationToken cancellationToken);
    /// <summary>执行前原子核对状态与任务停止标志，成功后才启动业务副作用。</summary>
    Task<bool> StartToolAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>保存插件实际结果后才接管草稿；此时插件可以释放其资格。</summary>
    Task SaveToolExecutionAsync(Guid id, ToolExecutionResult result, CancellationToken cancellationToken);
    /// <summary>保存完整宿主结局，失败不得重试已发生的动作。</summary>
    Task FinishToolAsync(AgentToolResult result, DateTimeOffset now, CancellationToken cancellationToken);
}
