using System.Collections.Immutable;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>处理器目录独立于管理服务组装，生命周期资格仍由原加载代次唯一管理。</summary>
public sealed class HandlerDirectory : IHandlerRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>登记已经初始化的描述；只有代次转为活动后才能取得调用资格。</summary>
    internal void Publish(PluginGeneration owner)
    {
        ImmutableArray<HandlerDescriptor> descriptions = owner.Capture();
        lock (_gate)
        {
            if (_entries.TryGetValue(owner.PluginId, out Entry? existing) && existing.Owner is not null)
                throw new InvalidOperationException("同一插件已有处理器代次。");
            _entries[owner.PluginId] = new Entry(descriptions, owner);
        }
    }

    /// <summary>停用仅保留不持有插件对象的描述，使用户排序可以保留暂时不可用的位置。</summary>
    internal void Withdraw(string pluginId, Guid generation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pluginId, out Entry? entry) && entry.Owner?.Id == generation)
                _entries[pluginId] = entry with { Owner = null };
        }
    }

    /// <summary>离开目录锁再检查代次，避免发布和停止过程出现反向锁顺序。</summary>
    public ImmutableArray<HandlerDescriptor> Capture(string hookId, HandlerKind kind)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        ImmutableArray<HandlerDescriptor>.Builder result = ImmutableArray.CreateBuilder<HandlerDescriptor>();
        foreach (Entry entry in entries)
        {
            bool enabled = entry.Owner?.IsActive == true;
            foreach (HandlerDescriptor descriptor in entry.Descriptions)
                if (descriptor.HookId == hookId && descriptor.Kind == kind) result.Add(descriptor with { Enabled = enabled });
        }
        return result.ToImmutable();
    }

    /// <summary>已经捕获的描述必须匹配原代次，不把旧调用交给新实现。</summary>
    public IHandlerLease? TryAcquire(HandlerDescriptor descriptor)
    {
        PluginGeneration? owner;
        lock (_gate) owner = _entries.GetValueOrDefault(descriptor.Key.PluginId)?.Owner;
        return owner?.TryAcquire(descriptor);
    }

    /// <summary>停用后 Owner 为空，只保留共享描述而不保活加载上下文。</summary>
    private sealed record Entry(ImmutableArray<HandlerDescriptor> Descriptions, PluginGeneration? Owner);
}
