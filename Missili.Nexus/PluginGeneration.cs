using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>宿主提供的共享服务，不包括根 DI 容器或插件专有类型。</summary>
public sealed record PluginHostServices
{
    public required IMessageHistory History { get; init; }
    public required IPlatformEventHistory Events { get; init; }
    public required IMessageIngress Ingress { get; init; }
    public required IPlatformEventIngress EventIngress { get; init; }
    public required AdapterDirectory Adapters { get; init; }
    public required ModelProviderDirectory ModelProviders { get; init; }
    public required IModelModule Models { get; init; }
    public required ISendReceiptSink Receipts { get; init; }
    public required IMediaStore Media { get; init; }
    public required IMediaImporter MediaImporter { get; init; }
    public required IStructuredContentStore Contents { get; init; }
    public required IMediaTranscriptionStore Transcriptions { get; init; }
    public required IMessageSender Sender { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
}

/// <summary>一个加载代次的唯一生命周期所有者，登记调用、任务及资源。</summary>
internal sealed class PluginGeneration : IPluginContext, IHandlerRegistry, IPluginTaskScope, IAdapterRegistry, IModelProviderRegistry
{
    private readonly object _gate = new();
    private readonly PluginManifest _manifest;
    private readonly string _entryPath;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly HashSet<Lease> _calls = [];
    private readonly HashSet<Task> _jobs = [];
    private readonly HashSet<OwnedResource> _resources = [];
    private readonly AdapterDirectory _adapters;
    private readonly ModelProviderDirectory _modelProviders;
    private readonly IModelModule _models;
    private readonly List<ModelProviderRegistration> _modelRegistrations = [];
    private readonly List<AdapterRegistration> _adapterRegistrations = [];
    private readonly TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PluginLoadContext? _loadContext;
    private IPlugin? _instance;
    private Task? _stopTask;
    private PluginState _state = PluginState.Loading;
    private string? _error;
    public Guid Id { get; } = Guid.NewGuid();
    public string PluginId => _manifest.Id;
    public JsonElement Configuration { get; }
    public ILogger Logger { get; }
    public TimeProvider Clock => _clock;
    public IMessageHistory History { get; }
    public IPlatformEventHistory Events { get; }
    public IMessageIngress Ingress { get; }
    public IPlatformEventIngress EventIngress { get; }
    public ISendReceiptSink Receipts { get; }
    public IPlatformOperations Platform => _adapters;
    public IAdapterRegistry Adapters => this;
    public IModelProviderRegistry ModelProviders => this;
    public IModelService Models => _models;
    public IModelCatalog ModelCatalog => _models.Catalog;
    public IConversationProfileService Profiles => _models.Profiles;
    public IConversationContextService ConversationContext => _models.Contexts;
    public IMediaTranscriptionService MediaTranscription => _models.Transcriptions;
    public IMediaRepresentationService MediaRepresentation => _models.Representations;
    public IModelCallHistory ModelCalls => _models.Calls;
    public IMediaStore Media { get; }
    public IMediaImporter MediaImporter { get; }
    public IStructuredContentStore Contents { get; }
    public IMediaTranscriptionStore Transcriptions { get; }
    public IMessageSender Sender { get; }
    public IHandlerRegistry Handlers => this;
    public IPluginTaskScope Tasks => this;
    public int MaxConcurrency { get; }

    /// <summary>准备值和资源容器；此时尚不执行入口代码。</summary>
    public PluginGeneration(PluginManifest manifest, string runtimeDirectory, PluginHostServices services, JsonElement configuration, InfrastructureOptions options, TimeProvider clock)
    {
        _manifest = manifest;
        _entryPath = PluginPackages.Within(runtimeDirectory, manifest.EntryAssembly);
        _timeout = options.PluginStopTimeout;
        _clock = clock;
        Configuration = configuration.Clone();
        Logger = services.LoggerFactory.CreateLogger("Plugin." + manifest.Id);
        History = services.History;
        Events = services.Events;
        Ingress = services.Ingress;
        EventIngress = services.EventIngress;
        Receipts = services.Receipts;
        _adapters = services.Adapters;
        _modelProviders = services.ModelProviders;
        _models = services.Models;
        Media = services.Media;
        MediaImporter = services.MediaImporter;
        Contents = services.Contents;
        Transcriptions = services.Transcriptions;
        Sender = services.Sender;
        MaxConcurrency = manifest.MaxConcurrency ?? options.PerPluginConcurrency;
        _loadContext = new PluginLoadContext(_entryPath);
    }

    /// <summary>加载入口并初始化；只有正常完成才开放接单。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Assembly assembly = _loadContext!.LoadFromAssemblyPath(_entryPath);
        Type type = assembly.GetType(_manifest.EntryType, throwOnError: true)!;
        if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || !type.IsPublic || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidDataException("插件入口必须是具有公开无参构造函数的 IPlugin 实现。");
        _instance = (IPlugin)Activator.CreateInstance(type)!;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _instance.InitializeAsync(this, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
    }

    /// <summary>管理器已持有目录发布锁时激活注册，网络任务不能抢在处理器目录发布之前开始。</summary>
    internal void Activate()
    {
        lock (_gate)
        {
            if (_state != PluginState.Loading) throw new InvalidOperationException("插件初始化状态已发生变化。");
            _adapters.Publish(this, _adapterRegistrations);
            try { _modelProviders.Publish(this, _modelRegistrations); }
            catch { _adapters.Withdraw(Id); throw; }
            _state = PluginState.Active;
            _activated.TrySetResult();
        }
    }

    /// <summary>能力调用也使用同一个代次接单检查，不重复占用消息调度额度。</summary>
    internal IHandlerLease? TryAcquireCapability()
    {
        lock (_gate)
        {
            if (_state != PluginState.Active) return null;
            Lease lease = new(this, null, _lifetime.Token);
            _calls.Add(lease);
            return lease;
        }
    }

    /// <summary>适配器仅在初始化暂存，身份冲突在发布全部注册之前检查。</summary>
    public void Register(AdapterRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.BotAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.PlatformId);
        ArgumentNullException.ThrowIfNull(registration.Driver);
        lock (_gate)
        {
            if (_state != PluginState.Loading) throw new InvalidOperationException("适配器只能在初始化时注册。");
            if (_adapterRegistrations.Any(item => item.AdapterId == registration.AdapterId)) throw new ArgumentException("适配器标识重复。");
            _adapterRegistrations.Add(registration);
        }
    }

    /// <summary>只通知网络能力变化，不独立切换插件状态。</summary>
    public void NotifyChanged() => _adapters.NotifyChanged();

    /// <summary>协议能力只在初始化暂存，不允许运行中改变注册集合。</summary>
    public void Register(ModelProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration.Driver);
        if (!Enum.IsDefined(registration.Protocol)) throw new ArgumentException("模型协议无效。");
        lock (_gate)
        {
            if (_state != PluginState.Loading) throw new InvalidOperationException("提供商只能在初始化时注册。");
            if (_modelRegistrations.Any(item => item.Protocol == registration.Protocol)) throw new ArgumentException("协议重复注册。");
            _modelRegistrations.Add(registration);
        }
    }

    /// <summary>登记运行任务，等待初始化成功后再启动网络连接。</summary>
    public Task RunAfterActivation(Func<CancellationToken, Task> operation) => Run(token => WaitForActivationAsync(operation, token));

    /// <summary>后台任务在已发布注册后运行，初始化失败时取消等待。</summary>
    private async Task WaitForActivationAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    { await _activated.Task.WaitAsync(token).ConfigureAwait(false); await operation(token).ConfigureAwait(false); }

    /// <summary>产生不含插件对象的目录快照。</summary>
    public ImmutableArray<HandlerDescriptor> Capture()
    {
        lock (_gate) return _bindings.Values.Select(binding => binding.Descriptor).ToImmutableArray();
    }

    /// <summary>在同一锁中检查代次状态并登记调用，封闭重载与接单的竞争。</summary>
    public IHandlerLease? TryAcquire(HandlerDescriptor descriptor)
    {
        lock (_gate)
        {
            if (_state != PluginState.Active || descriptor.Generation != Id ||
                !_bindings.TryGetValue(descriptor.Key.HandlerId, out Binding? binding) ||
                binding.Descriptor.HookId != descriptor.HookId || binding.Descriptor.Kind != descriptor.Kind) return null;
            Lease lease = new(this, binding, _lifetime.Token);
            _calls.Add(lease);
            return lease;
        }
    }

    /// <summary>读取纯数据诊断，不能通过状态保存异常或类型引用。</summary>
    public PluginStatus Status()
    {
        lock (_gate) return new PluginStatus
        {
            Id = PluginId,
            Name = _manifest.Name,
            Version = _manifest.Version,
            Generation = Id,
            State = _state,
            Error = _error,
            ActiveCalls = _calls.Count + _jobs.Count,
            OwnedResources = _resources.Count,
            HasLiveGeneration = true
        };
    }

    /// <summary>初始化错误只保存可读类型信息；完整错误交给标准日志设施。</summary>
    public void ReportInitializationFailure(Exception exception)
    {
        Logger.LogError(exception, "插件 {PluginId} 初始化失败", PluginId);
        lock (_gate) { _state = PluginState.Faulted; _error = "插件初始化失败：" + exception.GetType().Name; }
    }

    /// <summary>只创建一次停止过程，超时返回时仍跟踪未完成的清理。</summary>
    public async Task<PluginStatus> StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        lock (_gate)
        {
            if (_state == PluginState.Stopped) return Status();
            if (_stopTask is null)
            {
                _state = PluginState.Stopping;
                _adapters.Withdraw(Id);
                _modelProviders.Withdraw(Id);
                _stopTask = Task.Run(CleanupAsync);
            }
            stop = _stopTask;
        }
        try { await stop.WaitAsync(_timeout, _clock, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                // 期限到达与清理结束可能同时发生，不能把已经完成的状态覆盖回超时。
                if (_state is PluginState.Stopping or PluginState.StopTimedOut)
                {
                    _state = PluginState.StopTimedOut;
                    _error = "插件停止超过等待期限，仍保持不可接单。";
                }
            }
        }
        return Status();
    }

    /// <summary>取消、停止回调与在途工作全部退出后才释放资源。</summary>
    private async Task CleanupAsync()
    {
        using CancellationTokenSource budget = new(_timeout, _clock);
        List<string> errors = [];
        Task cancellation = _lifetime.CancelAsync();
        Task callback = _instance is null ? Task.CompletedTask : Task.Run(() => _instance.StopAsync(budget.Token));
        Task[] activity;
        lock (_gate) activity = _jobs.Concat(_calls.Select(call => call.Finished.Task)).ToArray();
        // 所有工作都需实际结束，某一个失败不能跳过其他任务的等待。
        foreach (Task task in activity.Append(cancellation).Append(callback))
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                errors.Add(exception.GetType().Name);
                Logger.LogError(exception, "插件 {PluginId} 停止工作失败", PluginId);
            }
        }
        OwnedResource[] resources;
        lock (_gate) resources = _resources.ToArray();
        foreach (OwnedResource resource in resources)
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                errors.Add(exception.GetType().Name);
                Logger.LogError(exception, "插件 {PluginId} 释放资源失败", PluginId);
            }
        }
        lock (_gate)
        {
            _state = errors.Count == 0 ? PluginState.Stopped : PluginState.Faulted;
            if (errors.Count > 0) _error = "插件清理失败：" + string.Join(", ", errors.Distinct());
        }
    }

    /// <summary>清除强引用后发起卸载，返回弱引用供宿主观察实际回收。
    /// 依据：https://learn.microsoft.com/dotnet/standard/assembly/unloadability 。</summary>
    public WeakReference Unload()
    {
        PluginLoadContext context;
        lock (_gate)
        {
            if (_state != PluginState.Stopped || _calls.Count != 0 || _jobs.Count != 0 || _resources.Count != 0)
                throw new InvalidOperationException("插件尚未完成停止，不能卸载。");
            context = _loadContext ?? throw new InvalidOperationException("插件已发起卸载。");
            _loadContext = null;
            _instance = null;
            _bindings.Clear();
            _adapterRegistrations.Clear();
            _modelRegistrations.Clear();
            _stopTask = null;
        }
        _lifetime.Dispose();
        WeakReference reference = new(context);
        context.Unload();
        return reference;
    }

    /// <summary>登记当前消息拦截器，不允许在初始化之外变更处理器集合。</summary>
    public void RegisterInterceptor(string id, string name, string description, int priority, Func<MessageContext, CancellationToken, Task<HandlerResult>> handler) =>
        Register(MessageHooks.Received, id, name, description, priority, HandlerKind.Interceptor, handler, null, null);

    /// <summary>登记独立通知处理器，包装结果不授予其拦截权。</summary>
    public void RegisterListener(string hookId, string id, string name, string description, int priority, Func<MessageContext, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(hookId, id, name, description, priority, HandlerKind.Listener, Invoke, null, null);
        /// <summary>等待监听调用真实完成，但不提供传播结果。</summary>
        async Task<HandlerResult> Invoke(MessageContext message, CancellationToken token)
        { await handler(message, token).ConfigureAwait(false); return new HandlerResult(); }
    }

    /// <summary>登记先匹配后执行的命令，执行失败不会撤销此前的接管。</summary>
    public void RegisterCommand(string id, string name, string description, string commandName, int priority, Func<MessageContext, CancellationToken, Task> handler, Func<MessageContext, bool>? matches = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        if (commandName.Any(char.IsWhiteSpace)) throw new ArgumentException("命令名不能包含空白字符。");
        ArgumentNullException.ThrowIfNull(handler);
        Register(MessageHooks.Received, id, name, description, priority, HandlerKind.Command, Invoke, matches, commandName);
        /// <summary>命令执行正常结束后仍表示已接管，而不是继续传播。</summary>
        async Task<HandlerResult> Invoke(MessageContext message, CancellationToken token)
        { await handler(message, token).ConfigureAwait(false); return new HandlerResult { Outcome = HandlerOutcome.Handled }; }
    }

    /// <summary>在初始化登记表中加入唯一稳定处理器。</summary>
    private void Register(string hookId, string id, string name, string description, int priority, HandlerKind kind,
        Func<MessageContext, CancellationToken, Task<HandlerResult>>? handler, Func<MessageContext, bool>? matches, string? commandName,
        Func<PlatformEventSnapshot, CancellationToken, Task>? eventHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (hookId == MessageHooks.PlatformEvent) ArgumentNullException.ThrowIfNull(eventHandler);
        else ArgumentNullException.ThrowIfNull(handler);
        if (hookId is not (MessageHooks.Received or MessageHooks.Sent or MessageHooks.PlatformEvent)) throw new ArgumentException("不支持的钩子。");
        lock (_gate)
        {
            if (_state != PluginState.Loading) throw new InvalidOperationException("处理器只能在插件初始化时注册。");
            HandlerDescriptor descriptor = new()
            {
                Key = new HandlerKey { PluginId = PluginId, HandlerId = id },
                Generation = Id,
                HookId = hookId,
                Kind = kind,
                Name = name,
                Description = description,
                Priority = priority,
                MaxConcurrency = MaxConcurrency,
                CommandName = commandName
            };
            if (!_bindings.TryAdd(id, new Binding { Descriptor = descriptor, Invoke = handler, Matches = matches, Event = eventHandler }))
                throw new ArgumentException("插件处理器标识重复。");
        }
    }

    /// <summary>事件注册沿用处理器编排和加载代次，但只允许独立监听。</summary>
    public void RegisterEventListener(string id, string name, string description, int priority, Func<PlatformEventSnapshot, CancellationToken, Task> handler) =>
        Register(MessageHooks.PlatformEvent, id, name, description, priority, HandlerKind.Listener, null, null, null, handler);

    /// <summary>登记后才调度后台工作，完成即移除引用，不累计历史 Task。</summary>
    public Task Run(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            EnsureRegisteringWork();
            _jobs.Add(completion.Task);
        }
        _ = Task.Run(Execute);
        return completion.Task;

        /// <summary>在宿主登记范围中执行任务并观察失败，结束时释放所有权。</summary>
        async Task Execute()
        {
            Exception? failure = null;
            bool cancelled = false;
            try { await operation(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { cancelled = true; }
            catch (Exception exception)
            {
                Logger.LogError(exception, "插件 {PluginId} 后台工作失败", PluginId);
                failure = exception;
            }
            finally { lock (_gate) _jobs.Remove(completion.Task); }
            if (failure is not null) { completion.TrySetException(failure); _ = completion.Task.Exception; }
            else if (cancelled) completion.TrySetCanceled(new CancellationToken(true));
            else completion.TrySetResult();
        }
    }

    /// <summary>取得资源释放责任；调用者提前释放句柄也会立即注销登记。</summary>
    public IAsyncDisposable Track(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            EnsureRegisteringWork();
            OwnedResource owned = new(this, resource);
            _resources.Add(owned);
            return owned;
        }
    }

    /// <summary>停止接单后也停止接收新的后台工作和资源。</summary>
    private void EnsureRegisteringWork()
    {
        if (_state is not (PluginState.Loading or PluginState.Active)) throw new InvalidOperationException("插件已停止接收新工作。");
    }

    /// <summary>保存当前代次的实际委托，绝不放入持久化顺序快照。</summary>
    private sealed class Binding
    {
        public required HandlerDescriptor Descriptor { get; init; }
        public Func<MessageContext, CancellationToken, Task<HandlerResult>>? Invoke { get; init; }
        public Func<PlatformEventSnapshot, CancellationToken, Task>? Event { get; init; }
        public Func<MessageContext, bool>? Matches { get; init; }
    }

    /// <summary>调用资格只有显式释放后才算退出，令牌取消不会提前减少活动计数。</summary>
    private sealed class Lease : IHandlerLease
    {
        private PluginGeneration? _owner;
        private Binding? _binding;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LifetimeToken { get; }

        /// <summary>捕获当前调用所需委托及取消令牌。</summary>
        public Lease(PluginGeneration owner, Binding? binding, CancellationToken token)
        { _owner = owner; _binding = binding; LifetimeToken = token; }
        /// <summary>仅在资格存续期间执行命令匹配。</summary>
        public bool Matches(MessageContext message) => (_binding ?? throw new ObjectDisposedException(nameof(Lease))).Matches?.Invoke(message) ?? true;
        /// <summary>将令牌传入插件，并等待实际异步方法返回。</summary>
        public Task<HandlerResult> InvokeAsync(MessageContext message, CancellationToken cancellationToken) =>
            (_binding?.Invoke ?? throw new InvalidOperationException("该调用资格不是消息处理器。")).Invoke(message, cancellationToken);
        /// <summary>在相同代次资格下执行事件监听，停止时仍需等待实际返回。</summary>
        public Task InvokeEventAsync(PlatformEventSnapshot platformEvent, CancellationToken cancellationToken) =>
            (_binding?.Event ?? throw new InvalidOperationException("该调用资格不是事件处理器。")).Invoke(platformEvent, cancellationToken);
        /// <summary>移除归属并清除委托，使完成调用不会阻止程序集回收。</summary>
        public void Dispose()
        {
            PluginGeneration? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            _binding = null;
            lock (owner._gate) { owner._calls.Remove(this); Finished.TrySetResult(); }
        }
    }

    /// <summary>资源只有释放成功才注销；失败资源继续留在代次中以阻止虚假卸载。</summary>
    private sealed class OwnedResource : IAsyncDisposable
    {
        private readonly PluginGeneration _owner;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IAsyncDisposable? _resource;

        /// <summary>包装已登记资源，不立即执行任何释放动作。</summary>
        public OwnedResource(PluginGeneration owner, IAsyncDisposable resource) { _owner = owner; _resource = resource; }
        /// <summary>串行执行资源释放，提前释放和宿主停止可安全汇合。</summary>
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_resource is null) return;
                await _resource.DisposeAsync().ConfigureAwait(false);
                _resource = null;
                lock (_owner._gate) _owner._resources.Remove(this);
            }
            finally { _gate.Release(); }
        }
    }
}
