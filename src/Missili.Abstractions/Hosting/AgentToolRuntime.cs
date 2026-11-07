using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>宿主调用资格覆盖真实准备或执行过程，释放时清除插件引用。</summary>
public interface IAgentToolLease : IDisposable
{
    Guid Generation { get; }
    CancellationToken LifetimeToken { get; }
    /// <summary>调用固定代次的无副作用准备函数。</summary>
    Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken);
    /// <summary>执行固定代次并将插件异常转换成共享结果。</summary>
    Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken);
}

/// <summary>工具目录只有描述和资格能力，不创建第二套插件生命周期。</summary>
public interface IAgentToolRuntime
{
    /// <summary>捕获包括暂时停用项的纯数据目录，权限过滤由调用方完成。</summary>
    ImmutableArray<AgentToolDescriptor> CaptureTools();
    /// <summary>取得原代次资格，过期描述不能自动切换到新代次。</summary>
    IAgentToolLease? TryAcquireTool(AgentToolDescriptor descriptor);
}
