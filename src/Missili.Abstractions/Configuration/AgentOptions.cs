using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>宿主 Agent 工作容量，等待和空闲不占实际执行额度。</summary>
public sealed record AgentOptions
{
    public bool Enabled { get; init; }
    public int MaximumTasks { get; init; } = 64;
    public int WakeAdmissionCapacity { get; init; } = 128;
    public long WakeAdmissionBytes { get; init; } = 8 * 1024 * 1024;
    public int MailboxCapacity { get; init; } = 64;
    public long MailboxBytes { get; init; } = 1024 * 1024;
    public int MaximumInputBytes { get; init; } = 64 * 1024;
    public int MaximumToolCallsPerResult { get; init; } = 8;
    public int MaximumToolArgumentBytes { get; init; } = 64 * 1024;
    public int MaximumToolResultBytes { get; init; } = 64 * 1024;
    public int MaximumJsonDepth { get; init; } = 64;

    /// <summary>容量必须能够接纳一条合法输入，非法组合在启动前拒绝。</summary>
    public void Validate()
    {
        int[] positive = [MaximumTasks, WakeAdmissionCapacity, MailboxCapacity, MaximumInputBytes,
            MaximumToolCallsPerResult,
            MaximumToolArgumentBytes, MaximumToolResultBytes, MaximumJsonDepth];
        if (positive.Any(value => value < 1) || MaximumToolResultBytes < 1024 || WakeAdmissionBytes < MaximumInputBytes || MailboxBytes < MaximumInputBytes) throw new ArgumentException("Agent 唤醒、邮箱或工具内容容量配置无效。");
    }
}
