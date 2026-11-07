namespace Missili.Abstractions;

/// <summary>一次业务动作的共享归属，模型请求、工具和出站记录使用同一组稳定身份。</summary>
public sealed record OperationOrigin
{
    public string? PluginId { get; init; }
    public Guid? TaskId { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? TurnId { get; init; }
    public Guid? ToolInvocationId { get; init; }
    public Guid? OutgoingBatchId { get; init; }
}
