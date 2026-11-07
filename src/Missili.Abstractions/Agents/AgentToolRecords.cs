using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>宿主冻结一次模型返回的具体工具请求，协议身份与执行身份分别保存。</summary>
public sealed record AgentToolIntent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ToolCallId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
    public AgentToolDescriptor? Tool { get; init; }
}

/// <summary>最终工具结果区分插件完成、宿主接管、发送回执和记录状态。</summary>
public sealed record AgentToolResult
{
    public required Guid InvocationId { get; init; }
    public required ToolCallState State { get; init; }
    public ToolExecutionResult? Execution { get; init; }
    public BatchSendResult? Sending { get; init; }
    public bool Recorded { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
}

/// <summary>可审计的一次调用；准备、批准、实际执行和最终结局分别保存。</summary>
public sealed record AgentToolRecord
{
    public required AgentToolIntent Intent { get; init; }
    public required OperationOrigin Origin { get; init; }
    public required Guid ModelCallId { get; init; }
    public required int Position { get; init; }
    public required ToolCallState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public PreparedToolCall? Prepared { get; init; }
    public ToolApprovalRequest? ApprovalRequest { get; init; }
    public ToolApprovalResult? Approval { get; init; }
    public ToolExecutionResult? Execution { get; init; }
    public AgentToolResult? Result { get; init; }
    public bool DiagnosticFieldsOmitted { get; init; }
}

/// <summary>短事务保证意图与预算、审批及执行状态的写入顺序，不持有插件代码。</summary>
public interface IAgentToolHistory
{
    /// <summary>仅在获准会话内读取原始调用，不执行历史动作。</summary>
    Task<AgentToolRecord?> GetToolCallAsync(MessageAddress conversation, Guid id, CancellationToken cancellationToken = default);
}
