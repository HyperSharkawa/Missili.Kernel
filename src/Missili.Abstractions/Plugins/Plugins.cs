using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Missili.Abstractions;

/// <summary>消息与平台事件的实际分发钩子。</summary>
public static class MessageHooks
{
    public const string Received = "message.received";
    public const string Sent = "message.sent";
    public const string PlatformEvent = "platform.event";
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
    public ImmutableArray<string> CommandPath { get; init; } = [];
}

/// <summary>原始历史和本次处理内容分开，后者不能伪造来源。</summary>
public sealed record MessageContext
{
    public required MessageSnapshot OriginalMessage { get; init; }
    public required ImmutableArray<MessageSegment> CurrentContent { get; init; }
    public CommandMatch? Command { get; init; }
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
    void RegisterCommand(string id, string name, string description, ImmutableArray<string> commandPath, int priority, Func<MessageContext, CancellationToken, Task> handler, Func<MessageContext, bool>? matches = null);
    /// <summary>注册独立平台业务事件监听器，不将通知和请求伪装成聊天消息。</summary>
    void RegisterEventListener(string id, string name, string description, int priority, Func<PlatformEventSnapshot, CancellationToken, Task> handler);
}

/// <summary>插件后台工作与资源的生命周期登记。</summary>
public interface IPluginTaskScope
{
    /// <summary>先登记后启动任务，停止时请求取消并等待任务实际退出。</summary>
    Task Run(Func<CancellationToken, Task> operation);
    /// <summary>先登记工作，初始化与注册发布成功后才启动网络等运行任务。</summary>
    Task RunAfterActivation(Func<CancellationToken, Task> operation);
    /// <summary>取得资源所有权，返回可提前释放并注销登记的句柄。</summary>
    IAsyncDisposable Track(IAsyncDisposable resource);
}

/// <summary>插件可访问的宿主能力，不提供根容器或数据库实体。</summary>
public interface IPluginContext
{
    string PluginId { get; }
    JsonElement Configuration { get; }
    ILogger Logger { get; }
    TimeProvider Clock { get; }
    IMessageHistory History { get; }
    IMessageIngress Ingress { get; }
    IPlatformEventIngress EventIngress { get; }
    IPlatformEventHistory Events { get; }
    IMediaStore Media { get; }
    IMediaImporter MediaImporter { get; }
    IStructuredContentStore Contents { get; }
    IMediaTranscriptionStore Transcriptions { get; }
    IMessageSender Sender { get; }
    ISendReceiptSink Receipts { get; }
    IPlatformOperations Platform { get; }
    IAdapterRegistry Adapters { get; }
    IModelProviderRegistry ModelProviders { get; }
    IAgentToolRegistry Tools { get; }
    IAgentService Agents { get; }
    IAgentHistory AgentHistory { get; }
    IOutgoingHistory OutgoingHistory { get; }
    IOutgoingProcessorRegistry OutgoingProcessors { get; }
    IPluginConfigurationRegistry ConfigurationValidators { get; }
    IModelService Models { get; }
    IModelCatalog ModelCatalog { get; }
    IConversationProfileService Profiles { get; }
    IConversationContextService ConversationContext { get; }
    IMediaTranscriptionService MediaTranscription { get; }
    IMediaRepresentationService MediaRepresentation { get; }
    IModelCallHistory ModelCalls { get; }
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

/// <summary>编译插件的安装、加载、重载与停止管理入口。</summary>
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
