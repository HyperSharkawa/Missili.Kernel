using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>独立配置档和首匹配路由；完整校验后原子发布不可变快照。</summary>
public sealed class ConversationConfigurationService : IModelCatalog, IConversationProfileService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string? _directory;
    private readonly int _networkConcurrency;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly IPluginConfigurationRuntime _validators;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly object _publishGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(1);
    private FileSystemWatcher? _watcher;
    private Task? _watch;
    private ConfigurationState? _current;
    private long _revision;
    private Task? _disposal;
    private bool _closing;

    /// <summary>只有完整配置发布成功才唤醒宿主的有界媒体扫描。</summary>
    public event Action? Published;

    /// <summary>配置目录由宿主解析，不在构造函数里读取文件或启动任务。</summary>
    public ConversationConfigurationService(string? directory, int networkConcurrency, ILogger logger, TimeProvider clock, IPluginConfigurationRuntime validators)
    { _directory = directory is null ? null : Path.GetFullPath(directory); _networkConcurrency = networkConcurrency; _logger = logger; _clock = clock; _validators = validators; }

    /// <summary>宿主决定是否配置共享目录，未启用时不要求模型或 Agent 配置文件。</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) => _directory is null ? Task.CompletedTask : ReloadAsync(cancellationToken);

    /// <summary>按当前顺序解析完整地址，不进行字段继承或跨会话混合。</summary>
    public ConversationProfileSnapshot Resolve(MessageAddress address)
        => TryResolve(address) ?? throw new InvalidOperationException("会话配置尚未启用或发布。");

    /// <summary>同一次读取固定全目录修订，插件激活前不会混用不同配置版本。</summary>
    public ImmutableArray<ConversationProfileSnapshot> CaptureProfiles()
    {
        ConfigurationState? state = Volatile.Read(ref _current);
        return state is null ? [] : state.Profiles.Values.OrderBy(profile => profile.Id, StringComparer.Ordinal)
            .Select(profile => new ConversationProfileSnapshot { Revision = state.Revision, Profile = profile, Models = state.Models, Providers = state.Providers }).ToImmutableArray();
    }

    /// <summary>明确区分未配置与路由错误，无配置的基础消息服务不依赖模型模块。</summary>
    public ConversationProfileSnapshot? TryResolve(MessageAddress address)
    {
        address.Validate();
        ConfigurationState? state = Volatile.Read(ref _current);
        if (state is null) return null;
        ConversationRoute? route = state.Routes.FirstOrDefault(item => Matches(item, address));
        return new ConversationProfileSnapshot { Revision = state.Revision, Profile = state.Profiles[route?.ProfileId ?? state.DefaultProfileId], Models = state.Models, Providers = state.Providers };
    }

    /// <summary>读取当前模型声明，无配置时返回空目录。</summary>
    public ImmutableArray<ModelDefinition> GetModels() => Volatile.Read(ref _current)?.Models.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray() ?? [];
    /// <summary>返回凭据环境变量名称，绝不读取或返回密钥值。</summary>
    public ImmutableArray<ModelProviderDefinition> GetProviders() => Volatile.Read(ref _current)?.Providers.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray() ?? [];

    /// <summary>目录、各配置档及路由共同组成一次发布，不吞掉读取或校验错误。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_directory is null) throw new InvalidOperationException("未指定会话配置目录。");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _reload.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            CatalogFile catalog = await ReadAsync<CatalogFile>(Path.Combine(_directory, "catalog.json"), linked.Token).ConfigureAwait(false);
            RouteFile routes = await ReadAsync<RouteFile>(Path.Combine(_directory, "routes.json"), linked.Token).ConfigureAwait(false);
            ImmutableArray<ConversationProfile>.Builder profiles = ImmutableArray.CreateBuilder<ConversationProfile>();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(_directory, "profiles"), "*.json").Order(StringComparer.Ordinal))
                profiles.Add(await ReadAsync<ConversationProfile>(file, linked.Token).ConfigureAwait(false));
            await PublishAsync(new ConversationConfiguration { DefaultProfileId = routes.DefaultProfileId, Providers = catalog.Providers, Models = catalog.Models, Profiles = profiles.ToImmutable(), Routes = routes.Routes }, linked.Token).ConfigureAwait(false);
        }
        finally { _reload.Release(); }
    }

    /// <summary>发布调用方已构造的完整配置，同样执行全部校验并复制 JSON 所有权。</summary>
    public async Task PublishAsync(ConversationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        configuration = configuration with
        {
            Models = configuration.Models.Select(model => model with { Parameters = model.Parameters?.Clone() }).ToImmutableArray(),
            Profiles = configuration.Profiles.Select(profile => profile with { Plugins = profile.Plugins.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) }).ToImmutableArray()
        };
        using IPluginConfigurationUpdate update = await _validators.BeginUpdateAsync(cancellationToken).ConfigureAwait(false);
        await update.ValidateAsync(configuration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableDictionary<string, ModelDefinition> models = configuration.Models.ToImmutableDictionary(item => item.Id, item => item with { Parameters = item.Parameters?.Clone() }, StringComparer.Ordinal);
        lock (_publishGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ConfigurationState next = new()
            {
                Revision = Interlocked.Increment(ref _revision),
                DefaultProfileId = configuration.DefaultProfileId,
                Models = models,
                Providers = configuration.Providers.ToImmutableDictionary(item => item.Id, StringComparer.Ordinal),
                Profiles = configuration.Profiles.ToImmutableDictionary(item => item.Id, item => item with
                {
                    Plugins = item.Plugins.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
                }, StringComparer.Ordinal),
                Routes = configuration.Routes
            };
            Volatile.Write(ref _current, next);
        }
        Published?.Invoke();
    }

    /// <summary>监视文件并合并短时间变化；显式重载仍可由测试和管理消费者使用。</summary>
    public void StartWatching()
    {
        if (_directory is null) return;
        if (_watcher is not null) throw new InvalidOperationException("配置监视已启动。");
        _watcher = new FileSystemWatcher(_directory, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Renamed += Changed;
        _watcher.EnableRaisingEvents = true;
        _watch = WatchAsync();
    }

    /// <summary>文件回调只发一个有界唤醒信号，不在事件线程执行磁盘和模型工作。</summary>
    private void Changed(object sender, FileSystemEventArgs args) => _changes.Writer.TryWrite(0);

    /// <summary>无效配置保留上次发布版本，同时提供不含正文或凭据的诊断。</summary>
    private async Task WatchAsync()
    {
        try
        {
            await foreach (byte change in _changes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), _clock, _stop.Token).ConfigureAwait(false);
                while (_changes.Reader.TryRead(out byte unused)) { }
                try { await ReloadAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException)
                { _logger.LogError("会话配置更新失败，保留上一有效修订：{ErrorType}", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>只接受文档声明的字段，未知拼写不能默默退回默认值。</summary>
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        await using FileStream file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(file, JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("模型配置文件为空。");
    }

    /// <summary>校验所有交叉引用与资源限制，失败时尚未修改当前快照。</summary>
    private void Validate(ConversationConfiguration configuration)
    {
        if (configuration.Providers.IsDefault || configuration.Models.IsDefault || configuration.Profiles.IsDefaultOrEmpty || configuration.Routes.IsDefault)
            throw new ArgumentException("模型配置集合未初始化或没有配置档。");
        Dictionary<string, ModelProviderDefinition> providers = configuration.Providers.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, ModelDefinition> models = configuration.Models.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, ConversationProfile> profiles = configuration.Profiles.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!profiles.ContainsKey(configuration.DefaultProfileId)) throw new ArgumentException("默认配置档不存在。");
        foreach (ModelProviderDefinition provider in providers.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable);
            if (!Enum.IsDefined(provider.Protocol) || !provider.BaseUrl.IsAbsoluteUri || provider.BaseUrl.Scheme is not ("http" or "https") ||
                provider.BaseUrl.UserInfo.Length != 0 || provider.BaseUrl.Query.Length != 0 || provider.BaseUrl.Fragment.Length != 0 || provider.MaxConcurrency < 1 || provider.MaxConcurrency > _networkConcurrency)
                throw new ArgumentException("提供商地址、协议或并发限制无效。");
        }
        foreach (ModelDefinition model in models.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Id); ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
            if (!providers.ContainsKey(model.ProviderId) || model.MaxImages < 1 || model.MaxRequestBytes < 1 || model.AudioFormats.IsDefault ||
                model.Parameters.HasValue && model.Parameters.Value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("模型声明或提供商引用无效。");
        }
        foreach (ConversationProfile profile in profiles.Values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
            if (profile.ImageModels.IsDefault || profile.TranscriptionModels.IsDefault || profile.Plugins is null ||
                profile.MaximumMessages < 2 || profile.DiscardMessages < 1 || profile.DiscardMessages >= profile.MaximumMessages || profile.MaxAudioBytes < 1 ||
                !Enum.IsDefined(profile.Visibility) || !Enum.IsDefined(profile.Images) || !Enum.IsDefined(profile.Audio))
                throw new ArgumentException("会话配置档参数无效。");
            foreach (KeyValuePair<string, JsonElement> plugin in profile.Plugins)
                if (string.IsNullOrWhiteSpace(plugin.Key) || plugin.Value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("插件配置必须以稳定插件标识关联 JSON 对象。");
            foreach (string id in profile.ImageModels.Concat(profile.TranscriptionModels))
                if (!models.ContainsKey(id)) throw new ArgumentException("候选模型不存在：" + id);
            if (profile.ImageModels.Any(id => !models[id].Images || providers[models[id].ProviderId].Protocol == ModelProtocol.AudioTranscription) ||
                profile.TranscriptionModels.Any(id => providers[models[id].ProviderId].Protocol != ModelProtocol.AudioTranscription))
                throw new ArgumentException("模型用途与声明能力不一致。");
            if (profile.ImageTranscriptionEnabled && profile.ImageModels.IsEmpty || profile.AudioTranscriptionEnabled && profile.TranscriptionModels.IsEmpty)
                throw new ArgumentException("启用转述必须提供对应模型候选。");
            profile.Execution.Validate();
            profile.ImagePreparation.Validate();
            profile.Agent.Validate(models, providers);
            profile.Approvals.Validate(profile.Execution, models, providers);
            profile.Outgoing.Validate();
        }
        foreach (ConversationRoute route in configuration.Routes)
        {
            if (!profiles.ContainsKey(route.ProfileId)) throw new ArgumentException("路由目标配置档不存在。");
            foreach (string pattern in new[] { route.AdapterId, route.PlatformId, route.BotAccountId, route.ConversationKind, route.ConversationId })
                ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        }
    }

    /// <summary>简单通配符仅支持星号和问号，并保持平台标识区分大小写。</summary>
    private static bool Matches(ConversationRoute route, MessageAddress address) =>
        FileSystemName.MatchesSimpleExpression(route.AdapterId, address.AdapterId, false) &&
        FileSystemName.MatchesSimpleExpression(route.PlatformId, address.PlatformId, false) &&
        FileSystemName.MatchesSimpleExpression(route.BotAccountId, address.BotAccountId, false) &&
        FileSystemName.MatchesSimpleExpression(route.ConversationKind, address.ConversationKind, false) &&
        FileSystemName.MatchesSimpleExpression(route.ConversationId, address.ConversationId, false);

    /// <summary>停止监视并等待实际重载退出，避免关闭后仍发布新配置。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_publishGate)
        {
            _closing = true;
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    /// <summary>关闭后等待已经开始的文件读取退出，不在重试宿主停止时重复释放取消源。</summary>
    private async Task DisposeCoreAsync()
    {
        _watcher?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_watch is not null) await _watch.ConfigureAwait(false);
        await _reload.WaitAsync().ConfigureAwait(false);
        _reload.Release();
        Published = null;
        _stop.Dispose();
    }

    /// <summary>读取目录文件时不引入配置档逐字段继承。</summary>
    private sealed record CatalogFile
    {
        public required ImmutableArray<ModelProviderDefinition> Providers { get; init; }
        public required ImmutableArray<ModelDefinition> Models { get; init; }
    }
    /// <summary>单独保存有序路由和明确的默认目标。</summary>
    private sealed record RouteFile
    {
        public required string DefaultProfileId { get; init; }
        public ImmutableArray<ConversationRoute> Routes { get; init; } = [];
    }
    /// <summary>发布后只读的完整目录，不暴露未校验的文件对象。</summary>
    private sealed record ConfigurationState
    {
        public required long Revision { get; init; }
        public required string DefaultProfileId { get; init; }
        public required ImmutableDictionary<string, ModelDefinition> Models { get; init; }
        public required ImmutableDictionary<string, ModelProviderDefinition> Providers { get; init; }
        public required ImmutableDictionary<string, ConversationProfile> Profiles { get; init; }
        public required ImmutableArray<ConversationRoute> Routes { get; init; }
    }
}
