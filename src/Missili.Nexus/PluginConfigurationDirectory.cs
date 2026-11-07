using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>配置校验能力目录与配置发布屏障；实际资源仍只由插件代次拥有。</summary>
public sealed class PluginConfigurationDirectory : IPluginConfigurationRuntime
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publication = new(1, 1);
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>初始化成功后登记能力，代次切换到 Active 才能用于后续配置更新。</summary>
    internal void Publish(PluginGeneration owner, IPluginConfigurationValidator? validator)
    {
        if (validator is null) return;
        lock (_gate) _entries.Add(owner.Id, new Entry(owner, validator));
    }

    /// <summary>撤销时立即移除执行器强引用，在途调用的资格负责等待实际退出。</summary>
    internal void Withdraw(Guid generation) { lock (_gate) _entries.Remove(generation); }

    /// <summary>串行化配置发布与插件激活，不串行化普通模型、工具或消息调用。</summary>
    public async Task<IPluginConfigurationUpdate> BeginUpdateAsync(CancellationToken cancellationToken)
    {
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Update(this);
    }

    /// <summary>在发布资格内检查活动校验器，任何代次退出都使本次更新失败。</summary>
    private async Task ValidateAsync(ConversationConfiguration configuration, CancellationToken token)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        ImmutableDictionary<string, ModelDefinition> models = configuration.Models.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal);
        ImmutableDictionary<string, ModelProviderDefinition> providers = configuration.Providers.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (Entry entry in entries)
        {
            using IHandlerLease? lease = entry.Owner.TryAcquireCapability();
            if (lease is null) throw new InvalidOperationException("配置校验期间插件代次已停止。");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.LifetimeToken);
            foreach (ConversationProfile profile in configuration.Profiles)
                await InvokeAsync(entry.Validator, Context(entry.Owner.PluginId, profile, models, providers), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>未启用的命名空间可以保留，首次启用必须用新代次校验当前完整快照。</summary>
    internal static async Task ValidateLoadingAsync(string pluginId, IPluginConfigurationValidator? validator,
        ImmutableArray<ConversationProfileSnapshot> profiles, CancellationToken token)
    {
        if (validator is null) return;
        foreach (ConversationProfileSnapshot profile in profiles)
            await InvokeAsync(validator, Context(pluginId, profile.Profile, profile.Models, profile.Providers), token).ConfigureAwait(false);
    }

    /// <summary>只向校验器交付自己的共享 JSON 和只读模型引用。</summary>
    private static PluginConfigurationContext Context(string id, ConversationProfile profile,
        ImmutableDictionary<string, ModelDefinition> models, ImmutableDictionary<string, ModelProviderDefinition> providers) => new()
        { ProfileId = profile.Id, Configuration = profile.Plugins.TryGetValue(id, out JsonElement value) ? value : Empty, Models = models, Providers = providers };

    /// <summary>异常在代次边界转换为宿主错误，避免异常对象保活可回收程序集。</summary>
    private static async Task InvokeAsync(IPluginConfigurationValidator validator, PluginConfigurationContext context, CancellationToken token)
    {
        ImmutableArray<PluginConfigurationError> errors;
        try { errors = await validator.ValidateAsync(context, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception exception) { throw new InvalidOperationException("插件配置校验异常：" + exception.GetType().Name); }
        token.ThrowIfCancellationRequested();
        if (errors.IsDefault) throw new InvalidOperationException("插件校验没有返回有效错误集合。");
        if (!errors.IsEmpty) throw new ArgumentException("配置档 " + context.ProfileId + "：" + string.Join("；", errors.Select(error => error.Path + " " + error.Message)));
    }

    /// <summary>目录活动条目不在撤销后长期保留。</summary>
    private sealed record Entry(PluginGeneration Owner, IPluginConfigurationValidator Validator);

    /// <summary>一次更新只释放一次屏障，不保留插件对象。</summary>
    private sealed class Update : IPluginConfigurationUpdate
    {
        private PluginConfigurationDirectory? _owner;
        /// <summary>取得已锁定的目录，调用者完成发布后释放。</summary>
        public Update(PluginConfigurationDirectory owner) => _owner = owner;
        /// <summary>配置更新必须保持资格直到原子发布完成。</summary>
        public Task ValidateAsync(ConversationConfiguration configuration, CancellationToken cancellationToken)
            => (_owner ?? throw new ObjectDisposedException(nameof(Update))).ValidateAsync(configuration, cancellationToken);
        /// <summary>释放后其他配置或插件代次可以发布。</summary>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?._publication.Release();
    }
}
