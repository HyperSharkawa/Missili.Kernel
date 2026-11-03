using System.Collections.Immutable;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>持久化用户编排并发布顺序快照；发布锁不覆盖任何插件业务执行。</summary>
public sealed class HookOrderService : IHookOrderService
{
    private readonly IInfrastructureStore _store;
    private readonly IPluginRuntime _plugins;
    private readonly SemaphoreSlim _publication = new(1, 1);
    private readonly Dictionary<(string, HandlerKind), StoredHookOrder> _cache = [];

    /// <summary>组合持久化配置与运行时纯数据目录。</summary>
    public HookOrderService(IInfrastructureStore store, IPluginRuntime plugins) { _store = store; _plugins = plugins; }

    /// <summary>读取当前阶段有效顺序，不写配置、不运行处理器。</summary>
    public async Task<HookOrderView> GetAsync(string hookId, HandlerKind kind, CancellationToken cancellationToken = default)
    {
        ValidateHook(hookId, kind);
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return BuildView(hookId, kind, await LoadOrderAsync(hookId, kind, cancellationToken).ConfigureAwait(false)); }
        finally { _publication.Release(); }
    }

    /// <summary>一次捕获当前事件全部阶段，防止执行中途混用新旧编排。</summary>
    public async Task<ImmutableArray<HookOrderView>> CaptureAsync(string hookId, CancellationToken cancellationToken)
    {
        ValidateHook(hookId, HandlerKind.Listener);
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<HookOrderView>.Builder result = ImmutableArray.CreateBuilder<HookOrderView>();
            foreach (HandlerKind kind in Enum.GetValues<HandlerKind>())
                result.Add(BuildView(hookId, kind, await LoadOrderAsync(hookId, kind, cancellationToken).ConfigureAwait(false)));
            return result.ToImmutable();
        }
        finally { _publication.Release(); }
    }

    /// <summary>合并暂时不可见的旧位置，再以修订号提交用户顺序。</summary>
    public async Task<HookOrderView> SaveAsync(string hookId, HandlerKind kind, ImmutableArray<HandlerKey> order, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateHook(hookId, kind);
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<HandlerDescriptor> handlers = _plugins.Capture(hookId, kind);
            HashSet<HandlerKey> known = handlers.Select(item => item.Key).ToHashSet();
            if (order.IsDefault || order.Length != known.Count || order.Distinct().Count() != order.Length || order.Any(key => !known.Contains(key)))
                throw new ArgumentException("排序必须恰好包含此钩子阶段当前登记的所有处理器。");
            StoredHookOrder old = await LoadOrderAsync(hookId, kind, cancellationToken).ConfigureAwait(false);
            ImmutableArray<HandlerKey>.Builder merged = ImmutableArray.CreateBuilder<HandlerKey>();
            int position = 0;
            foreach (HandlerKey key in old.Entries)
            {
                // 不可见旧项保留位置，可见位置按新的用户顺序依次填充。
                if (!known.Contains(key)) merged.Add(key);
                else if (position < order.Length) merged.Add(order[position++]);
            }
            while (position < order.Length) merged.Add(order[position++]);
            StoredHookOrder saved = await _store.SaveHookOrderAsync(hookId, kind, merged.ToImmutable(), true, expectedRevision, cancellationToken).ConfigureAwait(false);
            _cache[(hookId, kind)] = saved;
            return BuildView(hookId, kind, saved);
        }
        finally { _publication.Release(); }
    }

    /// <summary>显式恢复默认，成功保存后才更新内存快照。</summary>
    public async Task<HookOrderView> ResetAsync(string hookId, HandlerKind kind, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateHook(hookId, kind);
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<HandlerKey> defaults = DefaultOrder(_plugins.Capture(hookId, kind)).Select(item => item.Key).ToImmutableArray();
            StoredHookOrder saved = await _store.SaveHookOrderAsync(hookId, kind, defaults, false, expectedRevision, cancellationToken).ConfigureAwait(false);
            _cache[(hookId, kind)] = saved;
            return BuildView(hookId, kind, saved);
        }
        finally { _publication.Release(); }
    }

    /// <summary>配置只在首次读取时访问数据库，变更由本服务统一发布。</summary>
    private async Task<StoredHookOrder> LoadOrderAsync(string hookId, HandlerKind kind, CancellationToken token)
    {
        if (_cache.TryGetValue((hookId, kind), out StoredHookOrder? cached)) return cached;
        StoredHookOrder stored = await _store.GetHookOrderAsync(hookId, kind, token).ConfigureAwait(false);
        _cache[(hookId, kind)] = stored;
        return stored;
    }

    /// <summary>已有用户顺序优先，新处理器只追加，不覆盖用户的位置。</summary>
    private HookOrderView BuildView(string hookId, HandlerKind kind, StoredHookOrder order)
    {
        HandlerDescriptor[] defaults = DefaultOrder(_plugins.Capture(hookId, kind)).ToArray();
        Dictionary<HandlerKey, HandlerDescriptor> remaining = defaults.ToDictionary(item => item.Key);
        ImmutableArray<HookOrderItem>.Builder items = ImmutableArray.CreateBuilder<HookOrderItem>();
        if (order.Customized)
        {
            foreach (HandlerKey key in order.Entries)
                if (remaining.Remove(key, out HandlerDescriptor? handler)) items.Add(new HookOrderItem { Handler = handler });
        }
        foreach (HandlerDescriptor handler in defaults)
        {
            if (remaining.Remove(handler.Key)) items.Add(new HookOrderItem { Handler = handler, IsNew = order.Customized });
        }
        return new HookOrderView { HookId = hookId, Kind = kind, Revision = order.Revision, Customized = order.Customized, Items = items.ToImmutable() };
    }

    /// <summary>优先级相同使用稳定标识，不依赖加载时间或哈希表顺序。</summary>
    private static IOrderedEnumerable<HandlerDescriptor> DefaultOrder(IEnumerable<HandlerDescriptor> handlers) => handlers
        .OrderByDescending(item => item.Priority).ThenBy(item => item.Key.PluginId, StringComparer.Ordinal).ThenBy(item => item.Key.HandlerId, StringComparer.Ordinal);

    /// <summary>只有实际存在的钩子和阶段可以编排。</summary>
    private static void ValidateHook(string hookId, HandlerKind kind)
    {
        if (hookId is not (MessageHooks.Received or MessageHooks.Sent or MessageHooks.PlatformEvent) || !Enum.IsDefined(kind)
            || (hookId == MessageHooks.PlatformEvent && kind != HandlerKind.Listener)) throw new ArgumentException("钩子或执行阶段无效。");
    }
}
