using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>工具目录维护共享描述，执行器和取消资格仍归属 Nexus 加载代次。</summary>
public sealed class AgentToolDirectory : IAgentToolRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>同时检查稳定身份与协议名称，防止模型名称映射在发布中被覆盖。</summary>
    internal void Publish(PluginGeneration owner, IReadOnlyList<(AgentToolDefinition Definition, IAgentToolHandler Handler)> registrations)
    {
        lock (_gate)
        {
            foreach ((AgentToolDefinition definition, IAgentToolHandler handler) in registrations)
            {
                if (_entries.TryGetValue(definition.Id, out Entry? existing) && existing.Descriptor.PluginId != owner.PluginId ||
                    _entries.Values.Any(entry => entry.Descriptor.PluginId != owner.PluginId && entry.Descriptor.Definition.Name == definition.Name))
                    throw new InvalidOperationException("工具稳定标识或协议名称与已有注册冲突。");
            }
            foreach (string id in _entries.Where(pair => pair.Value.Descriptor.PluginId == owner.PluginId).Select(pair => pair.Key).ToArray()) _entries.Remove(id);
            foreach ((AgentToolDefinition definition, IAgentToolHandler handler) in registrations)
            {
                AgentToolDescriptor descriptor = new()
                { Definition = definition with { Parameters = definition.Parameters.Clone() }, PluginId = owner.PluginId, Generation = owner.Id, MaxConcurrency = owner.MaxConcurrency };
                _entries.Add(definition.Id, new Entry(descriptor, owner, handler));
            }
        }
    }

    /// <summary>停用移除执行器强引用，保留纯数据描述以便准确报告暂不可用。</summary>
    internal void Withdraw(Guid generation)
    {
        lock (_gate)
        {
            foreach (string id in _entries.Where(pair => pair.Value.Descriptor.Generation == generation).Select(pair => pair.Key).ToArray())
            { Entry entry = _entries[id]; _entries[id] = entry with { Owner = null, Handler = null }; }
        }
    }

    /// <summary>离开目录锁再访问代次状态，不与激活路径形成反向锁顺序。</summary>
    public ImmutableArray<AgentToolDescriptor> CaptureTools()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        return entries.Select(entry => entry.Descriptor with { Enabled = entry.Owner?.IsActive == true })
            .OrderBy(descriptor => descriptor.Definition.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>既检查稳定 ID 又检查加载代次，旧描述不调用新版本实现。</summary>
    public IAgentToolLease? TryAcquireTool(AgentToolDescriptor descriptor)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(descriptor.Definition.Id, out entry);
        if (entry?.Owner is null || entry.Handler is null || entry.Descriptor.Generation != descriptor.Generation || entry.Descriptor.PluginId != descriptor.PluginId) return null;
        IHandlerLease? lease = entry.Owner.TryAcquireCapability();
        return lease is null ? null : new Call(entry.Descriptor, entry.Handler, lease);
    }

    /// <summary>协议名称采用三种协议共同支持的规则，参数模式只用于模型描述。</summary>
    internal static void Validate(AgentToolDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.OutputDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.PermissionCategory);
        if (definition.DeclaredPermissions.IsDefault || definition.DeclaredPermissions.Any(string.IsNullOrWhiteSpace) || definition.DeclaredPermissions.Distinct(StringComparer.Ordinal).Count() != definition.DeclaredPermissions.Length)
            throw new ArgumentException("工具附加权限声明无效。");
        if (definition.IsControl || definition.Id.StartsWith("agent.", StringComparison.Ordinal) || definition.Id is ApprovalActionIds.MessageSend or AgentToolIds.ReadContent or AgentToolIds.ReadTranscription ||
            definition.Name is "spawn_agent" or "list_agents" or "send_agent_message" or "followup_agent" or "wait_agents" or "stop_agent" or "finish_turn" or "read_content" or "read_transcription")
            throw new ArgumentException("框架控制能力和发送动作身份不能由普通插件注册。");
        if (!Regex.IsMatch(definition.Name, "^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            definition.Parameters.ValueKind != JsonValueKind.Object || definition.ExecutionTimeout is TimeSpan timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentException("工具协议名称、参数模式或期限无效。");
    }

    /// <summary>停用条目不保存执行器或加载代次引用。</summary>
    private sealed record Entry(AgentToolDescriptor Descriptor, PluginGeneration? Owner, IAgentToolHandler? Handler);

    /// <summary>准备与执行分别取得资格，审批等待不需要保活插件执行器。</summary>
    private sealed class Call : IAgentToolLease
    {
        private IAgentToolHandler? _handler;
        private IHandlerLease? _lease;
        private readonly bool _hasSideEffects;
        public Guid Generation { get; }
        public CancellationToken LifetimeToken { get; }

        /// <summary>绑定原代次及其真实取消令牌。</summary>
        public Call(AgentToolDescriptor descriptor, IAgentToolHandler handler, IHandlerLease lease)
        { Generation = descriptor.Generation; _handler = handler; _lease = lease; LifetimeToken = lease.LifetimeToken; _hasSideEffects = descriptor.Definition.HasSideEffects; }

        /// <summary>准备错误跨边界变成共享说明，不让插件异常对象进入长期记录。</summary>
        public async Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
        {
            IAgentToolHandler handler = _handler ?? throw new ObjectDisposedException(nameof(Call));
            try { return (await handler.PrepareAsync(context, arguments, cancellationToken).ConfigureAwait(false)).Freeze(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || LifetimeToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            catch (Exception exception) { return new PreparedToolCall { Error = "工具参数准备失败：" + exception.GetType().Name }; }
        }

        /// <summary>执行异常不证明没有副作用，只有声明为纯读取的工具才能明确保持未开始。</summary>
        public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
        {
            IAgentToolHandler handler = _handler ?? throw new ObjectDisposedException(nameof(Call));
            try { return (await handler.ExecuteAsync(context, prepared, cancellationToken).ConfigureAwait(false)).Freeze(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || LifetimeToken.IsCancellationRequested)
            {
                return new ToolExecutionResult
                {
                    State = ToolCallState.CancelledDuringExecution,
                    Complete = false,
                    Effect = _hasSideEffects ? ToolEffectState.Unknown : ToolEffectState.NotStarted,
                    Error = "工具执行期间取消。",
                    ErrorKind = "Cancelled"
                };
            }
            catch (Exception exception)
            {
                return new ToolExecutionResult
                {
                    State = _hasSideEffects ? ToolCallState.Unknown : ToolCallState.Failed,
                    Complete = false,
                    Effect = _hasSideEffects ? ToolEffectState.Unknown : ToolEffectState.NotStarted,
                    Error = "工具执行失败：" + exception.GetType().Name,
                    ErrorKind = "Plugin"
                };
            }
        }

        /// <summary>先移除插件执行器引用，再报告代次工作已实际退出。</summary>
        public void Dispose() { _handler = null; Interlocked.Exchange(ref _lease, null)?.Dispose(); }
    }
}
