using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>政策拒绝和审批执行失败分别表达；只有有效放行或明确关闭审批才可继续。</summary>
public enum ToolApprovalState { NotRequired, Allowed, Denied, Failed, Cancelled }

/// <summary>审批材料保留来源，不把工具解释或子代理文字提升为宿主权限事实。</summary>
public sealed record ToolApprovalEvidence
{
    public required string Kind { get; init; }
    public string? SenderId { get; init; }
    public Guid? AgentId { get; init; }
    public long? MessageId { get; init; }
    public required JsonElement Content { get; init; }
}

/// <summary>宿主已经核验权限和实际参数后提交的具体动作，审批者不能改写它。</summary>
public sealed record ToolApprovalRequest
{
    public required string ToolId { get; init; }
    public required string Description { get; init; }
    public required string PermissionCategory { get; init; }
    public bool HasSideEffects { get; init; }
    public Guid? Generation { get; init; }
    public required PreparedToolCall Prepared { get; init; }
    public required MessageAddress Conversation { get; init; }
    public required ConversationProfileSnapshot Profile { get; init; }
    public required OperationOrigin Origin { get; init; }
    public ImmutableArray<ToolApprovalEvidence> Evidence { get; init; } = [];
}

/// <summary>返回审批意见和真实模型调用身份，保存成功之后调用方才能开始动作。</summary>
public sealed record ToolApprovalResult
{
    public required ToolApprovalState State { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
    public Guid? ModelCallId { get; init; }
    public bool Permitted => State is ToolApprovalState.Allowed or ToolApprovalState.NotRequired;
}

/// <summary>工具和最终消息共用的审批服务，业务权限检查和实际执行由各自宿主入口完成。</summary>
public interface IToolApprovalService
{
    /// <summary>审核一次冻结动作，拒绝或故障时不执行，也不创建持久挂起任务。</summary>
    Task<ToolApprovalResult> ReviewAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
    /// <summary>执行前比较本动作相关政策与候选，不因无关全局修订变化重复审批。</summary>
    bool IsCurrent(ToolApprovalRequest request, ConversationProfileSnapshot current);
}
