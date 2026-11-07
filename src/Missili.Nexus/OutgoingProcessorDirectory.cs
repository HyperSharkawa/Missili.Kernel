using System.Collections.Immutable;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>出站处理器复用 Nexus 代次资格，停用的描述可继续用于识别必需处理器。</summary>
public sealed class OutgoingProcessorDirectory : IOutgoingProcessorRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<HandlerKey, Entry> _entries = [];

    /// <summary>初始化成功后发布本插件全部处理器，旧代次必须已经撤销。</summary>
    internal void Publish(PluginGeneration owner, IReadOnlyList<(OutgoingProcessorDefinition Definition, IOutgoingProcessor Processor)> registrations)
    {
        lock (_gate)
        {
            if (_entries.Values.Any(entry => entry.Descriptor.Key.PluginId == owner.PluginId && entry.Owner is not null))
                throw new InvalidOperationException("出站处理器的旧代次尚未撤销。");
            foreach (HandlerKey key in _entries.Keys.Where(key => key.PluginId == owner.PluginId).ToArray()) _entries.Remove(key);
            foreach ((OutgoingProcessorDefinition definition, IOutgoingProcessor processor) in registrations)
            {
                HandlerKey key = new() { PluginId = owner.PluginId, HandlerId = definition.Id };
                OutgoingProcessorDescriptor descriptor = new()
                { Key = key, Generation = owner.Id, Name = definition.Name, Description = definition.Description, MaxConcurrency = owner.MaxConcurrency };
                _entries.Add(key, new Entry(descriptor, owner, processor));
            }
        }
    }

    /// <summary>撤销资格并清除插件引用，描述本身不持有执行代码。</summary>
    internal void Withdraw(Guid generation)
    {
        lock (_gate)
        {
            foreach (HandlerKey key in _entries.Where(item => item.Value.Descriptor.Generation == generation).Select(item => item.Key).ToArray())
                _entries[key] = _entries[key] with { Owner = null, Processor = null };
        }
    }

    /// <summary>只在目录锁外读取代次状态，避免与初始化发布产生锁反转。</summary>
    public ImmutableArray<OutgoingProcessorDescriptor> CaptureProcessors()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        return entries.Select(entry => entry.Descriptor with { Enabled = entry.Owner?.IsActive == true })
            .OrderBy(entry => entry.Key.PluginId, StringComparer.Ordinal).ThenBy(entry => entry.Key.HandlerId, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>处理批次中的旧描述只取得原代次资格，不能混入新版本。</summary>
    public IOutgoingProcessorLease? TryAcquireProcessor(OutgoingProcessorDescriptor descriptor)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(descriptor.Key, out entry);
        if (entry?.Owner is null || entry.Processor is null || entry.Descriptor.Generation != descriptor.Generation) return null;
        IHandlerLease? lease = entry.Owner.TryAcquireCapability();
        return lease is null ? null : new Call(entry.Processor, lease);
    }

    /// <summary>活动条目持有处理器，撤销后仅保留共享描述。</summary>
    private sealed record Entry(OutgoingProcessorDescriptor Descriptor, PluginGeneration? Owner, IOutgoingProcessor? Processor);

    /// <summary>调用完成时释放处理器，异常转换为共享文本而不是长期保存异常对象。</summary>
    private sealed class Call : IOutgoingProcessorLease
    {
        private IOutgoingProcessor? _processor;
        private IHandlerLease? _lease;
        public CancellationToken LifetimeToken { get; }

        /// <summary>绑定当前处理器和真实代次令牌。</summary>
        public Call(IOutgoingProcessor processor, IHandlerLease lease) { _processor = processor; _lease = lease; LifetimeToken = lease.LifetimeToken; }

        /// <summary>只有正常完整返回才接管修改后的内容。</summary>
        public async Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken)
        {
            IOutgoingProcessor processor = _processor ?? throw new ObjectDisposedException(nameof(Call));
            try
            {
                OutgoingProcessingResult result = await processor.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
                return result with { Messages = result.Messages.Select(message => message.Freeze()).ToImmutableArray() };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || LifetimeToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (Exception exception) { return new OutgoingProcessingResult { Messages = [], Error = "处理器执行失败：" + exception.GetType().Name }; }
        }

        /// <summary>释放强引用后才通知 Nexus 此调用已经退出。</summary>
        public void Dispose() { _processor = null; Interlocked.Exchange(ref _lease, null)?.Dispose(); }
    }
}
