using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>管理插件代次；同一插件的管理操作串行化，其他插件不共享该操作锁。</summary>
public sealed class PluginManager : IPluginRuntime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly InfrastructureOptions _options;
    private readonly PluginHostServices _services;
    private readonly TimeProvider _clock;
    private readonly PluginPackages _packages;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _management = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>配置包目录、共享服务及可测试时钟，不立即加载任何插件。</summary>
    public PluginManager(InfrastructureOptions options, PluginHostServices services, TimeProvider? clock = null)
    {
        options.Validate();
        _options = options;
        _services = services;
        _clock = clock ?? TimeProvider.System;
        _packages = new PluginPackages(options.DataDirectory, options.PerPluginConcurrency);
    }

    /// <summary>安装本地编译包，不触发加载和业务执行。</summary>
    public Task<string> InstallAsync(string zipPath, CancellationToken cancellationToken = default) => _packages.InstallAsync(zipPath, cancellationToken);

    /// <summary>读取并检查包后加载；已有未清理代次时拒绝并行创建。</summary>
    public async Task<PluginStatus> LoadAsync(string directory, JsonElement configuration, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        PluginManifest manifest = await _packages.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
        SemaphoreSlim management = _management.GetOrAdd(manifest.Id, _ => new SemaphoreSlim(1, 1));
        await management.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_slots.TryGetValue(manifest.Id, out Slot? existing) && existing.Current is not null)
                    throw new InvalidOperationException("插件已有代次，请先停用或使用重载操作。");
            }
            return await LoadCoreAsync(manifest, directory, configuration, cancellationToken).ConfigureAwait(false);
        }
        finally { management.Release(); }
    }

    /// <summary>预检查新包后停止旧代次，停止失败时不初始化新版本。</summary>
    public async Task<PluginStatus> ReloadAsync(string pluginId, string directory, JsonElement configuration, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        PluginManifest manifest = await _packages.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
        if (manifest.Id != pluginId) throw new ArgumentException("重载包与插件标识不一致。");
        SemaphoreSlim management = _management.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await management.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginStatus old = await StopCoreAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (old.State is not (PluginState.Stopped or PluginState.Faulted) || HasCurrent(pluginId)) return old;
            return await LoadCoreAsync(manifest, directory, configuration, cancellationToken).ConfigureAwait(false);
        }
        finally { management.Release(); }
    }

    /// <summary>重复停止共享同一清理过程，不重复执行插件停止回调。</summary>
    public async Task<PluginStatus> StopAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim management = _management.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await management.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StopCoreAsync(pluginId, cancellationToken).ConfigureAwait(false); }
        finally { management.Release(); }
    }

    /// <summary>只读取当前状态和上一卸载弱引用，不触发 GC 或文件清理。</summary>
    public ImmutableArray<PluginStatus> GetStatuses()
    {
        lock (_gate) return _slots.Values.Select(StatusOf).ToImmutableArray();
    }

    /// <summary>返回最后登记的纯数据目录，停用项保留但明确标记不可执行。</summary>
    public ImmutableArray<HandlerDescriptor> Capture(string hookId, HandlerKind kind)
    {
        lock (_gate) return _slots.Values.SelectMany(slot => slot.Known.Select(handler => handler with
        { Enabled = slot.Current?.Status().State == PluginState.Active && slot.Current.Id == handler.Generation }))
            .Where(handler => handler.HookId == hookId && handler.Kind == kind).ToImmutableArray();
    }

    /// <summary>代次不匹配或不再接单时立即返回不可用，不改用新代次。</summary>
    public IHandlerLease? TryAcquire(HandlerDescriptor descriptor)
    {
        PluginGeneration? generation;
        lock (_gate) generation = _slots.TryGetValue(descriptor.Key.PluginId, out Slot? slot) ? slot.Current : null;
        return generation?.TryAcquire(descriptor);
    }

    /// <summary>供诊断与卸载验收读取上一代次的弱引用，不暴露程序集对象。</summary>
    public WeakReference? GetLastUnloadReference(string pluginId)
    {
        lock (_gate) return _slots.TryGetValue(pluginId, out Slot? slot) ? slot.LastUnload : null;
    }

    /// <summary>管理操作已持有插件锁时创建、初始化并发布代次。</summary>
    private async Task<PluginStatus> LoadCoreAsync(PluginManifest manifest, string directory, JsonElement configuration, CancellationToken token)
    {
        string runtime = _packages.CreateRuntime(directory, manifest.Id, Guid.NewGuid(), token);
        PluginGeneration generation = new(manifest, runtime, _services, configuration, _options, _clock);
        Slot slot;
        lock (_gate)
        {
            if (!_slots.TryGetValue(manifest.Id, out slot!)) _slots[manifest.Id] = slot = new Slot { Last = generation.Status() };
            slot.Current = generation;
        }
        try
        {
            await generation.InitializeAsync(token).ConfigureAwait(false);
            lock (_gate) { generation.Activate(); slot.Known = generation.Capture(); slot.Last = generation.Status(); }
            return generation.Status();
        }
        catch (Exception exception)
        {
            generation.ReportInitializationFailure(exception);
            string error = generation.Status().Error!;
            PluginStatus stopped = await generation.StopAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                slot.Last = stopped with { State = PluginState.Faulted, Error = error };
                if (stopped.State == PluginState.Stopped)
                {
                    slot.LastUnload = generation.Unload();
                    slot.LastUnloadId = generation.Id;
                    slot.Current = null;
                }
            }
            if (exception is OperationCanceledException && token.IsCancellationRequested) throw;
            return GetStatuses().Single(item => item.Id == manifest.Id);
        }
    }

    /// <summary>等待真实清理结束才移除代次并发起卸载。</summary>
    private async Task<PluginStatus> StopCoreAsync(string pluginId, CancellationToken token)
    {
        Slot slot;
        PluginGeneration? current;
        lock (_gate)
        {
            if (!_slots.TryGetValue(pluginId, out slot!)) throw new KeyNotFoundException("插件尚未加载。");
            current = slot.Current;
            if (current is null) return StatusOf(slot);
        }
        PluginStatus status = await current.StopAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            slot.Last = status;
            if (status.State == PluginState.Stopped)
            {
                slot.LastUnload = current.Unload();
                slot.LastUnloadId = current.Id;
                slot.Current = null;
            }
            return StatusOf(slot);
        }
    }

    /// <summary>判断是否仍有不能被替换的真实代次。</summary>
    private bool HasCurrent(string id) { lock (_gate) return _slots.TryGetValue(id, out Slot? slot) && slot.Current is not null; }

    /// <summary>将运行状态和回收观察组合为普通数据。</summary>
    private static PluginStatus StatusOf(Slot slot)
    {
        PluginStatus status = slot.Current?.Status() ?? slot.Last;
        return status with
        {
            HasLiveGeneration = slot.Current is not null,
            LastUnloadedGeneration = slot.LastUnloadId,
            UnloadRequested = slot.LastUnload is not null,
            Collected = slot.LastUnload is not null && !slot.LastUnload.IsAlive
        };
    }

    /// <summary>配置是明确的 JSON 对象，不把非法输入静默变成空配置。</summary>
    private static void ValidateConfiguration(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object) throw new ArgumentException("插件配置必须是 JSON 对象。");
    }

    /// <summary>停止所有插件，超时状态仍保留在管理器中而不强行卸载。</summary>
    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_gate) ids = _slots.Keys.ToArray();
        foreach (string id in ids) await StopAsync(id).ConfigureAwait(false);
    }

    /// <summary>只保留当前代次和最后一次诊断，不累计已完成 Task 或异常。</summary>
    private sealed class Slot
    {
        public PluginGeneration? Current { get; set; }
        public required PluginStatus Last { get; set; }
        public ImmutableArray<HandlerDescriptor> Known { get; set; } = [];
        public WeakReference? LastUnload { get; set; }
        public Guid? LastUnloadId { get; set; }
    }
}
