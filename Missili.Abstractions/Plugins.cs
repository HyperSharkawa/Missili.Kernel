using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Missili.Abstractions;

/// <summary>本阶段实际分发的钩子；不预建模型或工具专有钩子。</summary>
public static class MessageHooks
{
    public const string Received = "message.received";
    public const string Sent = "message.sent";
}

/// <summary>三种调用阶段，用户排序不能跨阶段改变传播语义。</summary>
public enum HandlerKind { Interceptor, Command, Listener }

/// <summary>拦截器正常返回后才应用的传播决策。</summary>
public enum HandlerOutcome { Continue, Replace, Handled }

/// <summary>不依赖程序集版本或展示名称的处理器身份。</summary>
public sealed record HandlerKey
{
    public required string PluginId { get; init; }
    public required string HandlerId { get; init; }
}

/// <summary>分发快照只保留值和代次，不保存插件委托。</summary>
public sealed record HandlerDescriptor
{
    public required HandlerKey Key { get; init; }
    public required Guid Generation { get; init; }
    public required string HookId { get; init; }
    public required HandlerKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int Priority { get; init; }
    public int MaxConcurrency { get; init; }
    public bool Enabled { get; init; } = true;
    public string? CommandName { get; init; }
}

/// <summary>原始历史和本次处理内容分开，后者不能伪造来源。</summary>
public sealed record MessageContext
{
    public required MessageSnapshot OriginalMessage { get; init; }
    public required ImmutableArray<MessageSegment> CurrentContent { get; init; }
}

/// <summary>处理器的完整返回值；异常结束不会应用部分修改。</summary>
public sealed record HandlerResult
{
    public HandlerOutcome Outcome { get; init; }
    public ImmutableArray<MessageSegment> Content { get; init; } = [];
}

/// <summary>注册仅归属当前插件代次，初始化完成之前不对外分发。</summary>
public interface IHandlerRegistry
{
    /// <summary>注册消息拦截器，数值较大的默认优先级先执行。</summary>
    void RegisterInterceptor(string id, string name, string description, int priority, Func<MessageContext, CancellationToken, Task<HandlerResult>> handler);
    /// <summary>注册独立监听器，仅支持当前内核实际分发的钩子。</summary>
    void RegisterListener(string hookId, string id, string name, string description, int priority, Func<MessageContext, CancellationToken, Task> handler);
    /// <summary>注册先匹配再执行的文本命令；匹配成功即完成接管。</summary>
    void RegisterCommand(string id, string name, string description, string commandName, int priority, Func<MessageContext, CancellationToken, Task> handler, Func<MessageContext, bool>? matches = null);
}

/// <summary>插件后台工作与资源的生命周期登记。</summary>
public interface IPluginTaskScope
{
    /// <summary>先登记后启动任务，停止时请求取消并等待任务实际退出。</summary>
    Task Run(Func<CancellationToken, Task> operation);
    /// <summary>取得资源所有权，返回可提前释放并注销登记的句柄。</summary>
    IAsyncDisposable Track(IAsyncDisposable resource);
}

/// <summary>插件可访问的宿主能力，不提供根容器或数据库实体。</summary>
public interface IPluginContext
{
    string PluginId { get; }
    JsonElement Configuration { get; }
    ILogger Logger { get; }
    IMessageHistory History { get; }
    IMediaStore Media { get; }
    IMediaTranscriptionStore Transcriptions { get; }
    IMessageSender Sender { get; }
    IHandlerRegistry Handlers { get; }
    IPluginTaskScope Tasks { get; }
}

/// <summary>编译插件入口；实现必须具有公开无参构造函数。</summary>
public interface IPlugin
{
    /// <summary>初始化并登记处理器和资源；成功后宿主一次性发布注册。</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
    /// <summary>停止插件自有工作；不能依赖强制中止线程完成回收。</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>插件状态区分不可接单、工作未退出与正常卸载。</summary>
public enum PluginState { Loading, Active, Stopping, Stopped, Faulted, StopTimedOut }

/// <summary>纯数据插件诊断，不持有插件异常对象或程序集引用。</summary>
public sealed record PluginStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required Guid Generation { get; init; }
    public required PluginState State { get; init; }
    public string? Error { get; init; }
    public int ActiveCalls { get; init; }
    public int OwnedResources { get; init; }
    public bool HasLiveGeneration { get; init; }
    public Guid? LastUnloadedGeneration { get; init; }
    public bool UnloadRequested { get; init; }
    public bool Collected { get; init; }
}

/// <summary>编译插件管理服务，第一阶段不提供无认证的 HTTP 包装。</summary>
public interface IPluginManager
{
    /// <summary>检查并安装本地 ZIP，返回已安装目录；不执行其中代码。</summary>
    Task<string> InstallAsync(string zipPath, CancellationToken cancellationToken = default);
    /// <summary>从校验后的目录加载插件，返回状态而不影响其他插件。</summary>
    Task<PluginStatus> LoadAsync(string directory, JsonElement configuration, CancellationToken cancellationToken = default);
    /// <summary>停止旧版本后加载指定版本；失败不回滚旧版本。</summary>
    Task<PluginStatus> ReloadAsync(string pluginId, string directory, JsonElement configuration, CancellationToken cancellationToken = default);
    /// <summary>请求停止并等待清理；超时仍保留工作跟踪。</summary>
    Task<PluginStatus> StopAsync(string pluginId, CancellationToken cancellationToken = default);
    /// <summary>读取插件当前纯数据状态。</summary>
    ImmutableArray<PluginStatus> GetStatuses();
}

/// <summary>内核调用资格，持有期间由 Nexus 跟踪，不允许旧代次接单。</summary>
public interface IHandlerLease : IDisposable
{
    CancellationToken LifetimeToken { get; }
    /// <summary>在已取得资格的调用中进行命令匹配。</summary>
    bool Matches(MessageContext message);
    /// <summary>执行已登记处理器，调用者负责捕获插件业务异常。</summary>
    Task<HandlerResult> InvokeAsync(MessageContext message, CancellationToken cancellationToken);
}

/// <summary>只供宿主实现使用的插件目录与原子调用入口。</summary>
public interface IPluginRuntime : IPluginManager
{
    /// <summary>生成不含委托的处理器快照。</summary>
    ImmutableArray<HandlerDescriptor> Capture(string hookId, HandlerKind kind);
    /// <summary>原子验证代次和接单状态，成功后登记调用。</summary>
    IHandlerLease? TryAcquire(HandlerDescriptor descriptor);
}

/// <summary>当前顺序中的一个处理器和新增标记。</summary>
public sealed record HookOrderItem
{
    public required HandlerDescriptor Handler { get; init; }
    public bool IsNew { get; init; }
}

/// <summary>钩子编排视图，供未来管理界面直接使用。</summary>
public sealed record HookOrderView
{
    public required string HookId { get; init; }
    public required HandlerKind Kind { get; init; }
    public required long Revision { get; init; }
    public required bool Customized { get; init; }
    public required ImmutableArray<HookOrderItem> Items { get; init; }
}

/// <summary>用户编排服务，使用修订号避免互相覆盖。</summary>
public interface IHookOrderService
{
    /// <summary>读取当前有效顺序，保留临时停用插件的已保存位置。</summary>
    Task<HookOrderView> GetAsync(string hookId, HandlerKind kind, CancellationToken cancellationToken = default);
    /// <summary>保存当前处理器排序；持久化失败时不发布新顺序。</summary>
    Task<HookOrderView> SaveAsync(string hookId, HandlerKind kind, ImmutableArray<HandlerKey> order, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>恢复插件默认顺序并增加修订号。</summary>
    Task<HookOrderView> ResetAsync(string hookId, HandlerKind kind, long expectedRevision, CancellationToken cancellationToken = default);
}
