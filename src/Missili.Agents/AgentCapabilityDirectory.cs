using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>组合插件工具与宿主内置控制工具，纯描述快照不携带执行代码。</summary>
public sealed class AgentCapabilityDirectory : IAgentToolRuntime
{
    private readonly IAgentToolRuntime _plugins;
    private readonly Guid _generation;
    private Builtins? _builtins;

    /// <summary>构造时尚不发布内置工具，Agent 正式登记唤醒时才绑定实际运行器。</summary>
    public AgentCapabilityDirectory(IAgentToolRuntime plugins, Guid generation) { _plugins = plugins; _generation = generation; }

    /// <summary>运行器拥有宿主控制工具的整个寿命，进程重启不会复用该代次。</summary>
    internal void Bind(IAgentToolHandler handler, ImmutableArray<AgentToolDefinition> definitions, int concurrency, CancellationToken lifetime)
    {
        Builtins value = new(handler, definitions.Select(definition => new AgentToolDescriptor
        { Definition = definition, PluginId = "missili.agents", Generation = _generation, MaxConcurrency = concurrency, Enabled = true }).ToImmutableArray(), lifetime);
        if (Interlocked.CompareExchange(ref _builtins, value, null) is not null) throw new InvalidOperationException("内置能力不能重复绑定。");
    }

    /// <summary>插件与框架工具一起提供给调用方做当前任务权限筛选。</summary>
    public ImmutableArray<AgentToolDescriptor> CaptureTools()
    {
        Builtins? builtins = Volatile.Read(ref _builtins);
        ImmutableArray<AgentToolDescriptor> plugins = _plugins.CaptureTools();
        return builtins is null ? plugins : plugins.AddRange(builtins.Tools.Select(tool => tool with { Enabled = !builtins.Lifetime.IsCancellationRequested }));
    }

    /// <summary>框架控制只在当前宿主代次内可用，普通工具继续经过 Nexus。</summary>
    public IAgentToolLease? TryAcquireTool(AgentToolDescriptor descriptor)
    {
        if (descriptor.PluginId != "missili.agents") return _plugins.TryAcquireTool(descriptor);
        Builtins? builtins = Volatile.Read(ref _builtins);
        return builtins is null || builtins.Lifetime.IsCancellationRequested || descriptor.Generation != _generation ||
            !builtins.Tools.Any(tool => tool.Definition.Id == descriptor.Definition.Id) ? null : new Call(builtins, descriptor);
    }

    /// <summary>宿主绑定包含实际处理器，生命周期由运行器令牌直接表示。</summary>
    private sealed record Builtins(IAgentToolHandler Handler, ImmutableArray<AgentToolDescriptor> Tools, CancellationToken Lifetime);

    /// <summary>内置调用也使用相同的准备和执行接口，实际工作由 Agent 推进器跟踪。</summary>
    private sealed class Call : IAgentToolLease
    {
        private Builtins? _owner;
        private readonly AgentToolDescriptor _tool;
        public Guid Generation => _tool.Generation;
        public CancellationToken LifetimeToken { get; }
        /// <summary>固定调用实际宿主工具，参数额外带上不可伪造的稳定身份。</summary>
        public Call(Builtins owner, AgentToolDescriptor tool) { _owner = owner; _tool = tool; LifetimeToken = owner.Lifetime; }
        /// <summary>稳定身份由目录附加，不接受模型通过 JSON 指定其他工具。</summary>
        public Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
            => (_owner ?? throw new ObjectDisposedException(nameof(Call))).Handler.PrepareAsync(context, JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["tool"] = _tool.Definition.Id, ["arguments"] = arguments }), cancellationToken);
        /// <summary>准备结果包含已经固定的工具身份，实际执行不查询另一个工具目录。</summary>
        public Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
            => (_owner ?? throw new ObjectDisposedException(nameof(Call))).Handler.ExecuteAsync(context, prepared, cancellationToken);
        /// <summary>清除调用引用，运行器会在工具组实际退出后释放自身资源。</summary>
        public void Dispose() => _owner = null;
    }
}
