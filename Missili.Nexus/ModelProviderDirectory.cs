using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>协议目录只借用 Nexus 的代次资格，不拥有独立启停状态机。</summary>
public sealed class ModelProviderDirectory : IModelProviderRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<ModelProtocol, Entry> _entries = [];

    /// <summary>检查全部冲突后发布一个代次的注册，失败不留下半份目录。</summary>
    internal void Publish(PluginGeneration owner, IReadOnlyList<ModelProviderRegistration> registrations)
    {
        lock (_gate)
        {
            if (registrations.Any(item => _entries.ContainsKey(item.Protocol)))
                throw new InvalidOperationException("模型协议已由其他插件注册。");
            foreach (ModelProviderRegistration registration in registrations)
                _entries.Add(registration.Protocol, new Entry(owner, registration.Driver));
        }
    }

    /// <summary>撤销可见注册；已取得的调用保留自身资格直到真实退出。</summary>
    internal void Withdraw(Guid generation)
    {
        lock (_gate)
        {
            ModelProtocol[] protocols = _entries.Where(item => item.Value.Owner.Id == generation).Select(item => item.Key).ToArray();
            foreach (ModelProtocol protocol in protocols) _entries.Remove(protocol);
        }
    }

    /// <summary>不在目录锁内获取代次锁，避免与发布和停止形成反向锁顺序。</summary>
    public IModelProviderLease? TryAcquire(ModelProtocol protocol)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(protocol, out entry);
        if (entry is null) return null;
        IHandlerLease? lease = entry.Owner.TryAcquireCapability();
        return lease is null ? null : new Call(entry.Owner.Id, entry.Driver, lease);
    }

    /// <summary>仅活动目录持有协议驱动，停用时删除条目。</summary>
    private sealed record Entry(PluginGeneration Owner, IModelProtocolDriver Driver);

    /// <summary>释放时清除插件引用；完成的调用对象不会阻止程序集回收。</summary>
    private sealed class Call : IModelProviderLease
    {
        private IModelProtocolDriver? _driver;
        private IHandlerLease? _lease;
        public Guid Generation { get; }
        public CancellationToken LifetimeToken { get; }
        /// <summary>捕获单一代次的资格和驱动。</summary>
        public Call(Guid generation, IModelProtocolDriver driver, IHandlerLease lease)
        { Generation = generation; _driver = driver; _lease = lease; LifetimeToken = lease.LifetimeToken; }
        /// <summary>请求计量沿用该代次驱动，停止后不能切换到新版本。</summary>
        public Task<long> MeasureAsync(ModelAttemptRequest request, CancellationToken cancellationToken) =>
            (_driver ?? throw new ObjectDisposedException(nameof(Call))).MeasureAsync(request, cancellationToken);
        /// <summary>只调用已经取得资格的代次，不重定向到新版本。</summary>
        public Task<ModelProtocolResult> ExecuteAsync(ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken cancellationToken) =>
            (_driver ?? throw new ObjectDisposedException(nameof(Call))).ExecuteAsync(request, observer, cancellationToken);
        /// <summary>先释放驱动强引用，再报告代次调用退出。</summary>
        public void Dispose() { _driver = null; Interlocked.Exchange(ref _lease, null)?.Dispose(); }
    }
}
