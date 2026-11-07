using Missili.Abstractions;
using Missili.Agents;
using Missili.Kernel;
using Missili.Matrix;
using Missili.Models;
using Missili.Nexus;
using Missili.Persistence;

namespace Missili.Host;

/// <summary>唯一的基础设施组装位置，运行项目之间保持契约依赖。</summary>
public static class InfrastructureFactory
{
    /// <summary>创建拥有完整资源生命周期的运行时；传输集合为空即没有平台接入。</summary>
    public static InfrastructureRuntime Create(InfrastructureOptions options, ILoggerFactory loggerFactory,
        IEnumerable<IMessageTransport>? transports = null, TimeProvider? clock = null, ModelOptions? modelOptions = null, AgentOptions? agentOptions = null, PluginExecutionOptions? pluginExecutionOptions = null)
    {
        options.Validate();
        ModelOptions modelSettings = modelOptions ?? new ModelOptions();
        AgentOptions agentSettings = agentOptions ?? new AgentOptions();
        agentSettings.Validate();
        PluginExecutionOptions pluginExecution = pluginExecutionOptions ?? new PluginExecutionOptions();
        pluginExecution.Validate();
        if (agentSettings.Enabled && !modelSettings.Enabled) throw new ArgumentException("启用 Agent 必须同时启用模型模块。");
        if (modelSettings.Enabled && options.ConversationConfigurationDirectory is null)
            throw new ArgumentException("启用模型模块必须指定共享会话配置目录。");
        TimeProvider time = clock ?? TimeProvider.System;
        Guid runId = Guid.NewGuid();
        SqliteStore store = new(Path.Combine(options.DataDirectory, "history.db"), options);
        DispatchSignal signal = new();
        AdapterDirectory adapters = new();
        ModelProviderDirectory modelProviders = new();
        HandlerDirectory handlers = new();
        AgentToolDirectory tools = new();
        AgentCapabilityDirectory capabilities = new(tools, runId);
        OutgoingProcessorDirectory outgoing = new();
        PluginConfigurationDirectory validators = new();
        FairInvocationScheduler toolScheduler = new(pluginExecution.ToolsAndProcessors.Concurrency, pluginExecution.ToolsAndProcessors.QueueCapacity);
        HookOrderService orders = new(store, handlers);
        IngressRouter ingress = new();
        MediaArchive media = new(store, options, loggerFactory.CreateLogger<MediaArchive>(), time, adapters);
        StructuredContentArchive contents = new(store, adapters, media, options, loggerFactory.CreateLogger<StructuredContentArchive>(), time);
        ConversationConfigurationService profiles = new(options.ConversationConfigurationDirectory, modelSettings.NetworkConcurrency, loggerFactory.CreateLogger<ConversationConfigurationService>(), time, validators);
        ModelRuntime models = new(store, store, media, media, adapters, contents, modelProviders, profiles, modelSettings, options.DataDirectory, runId, loggerFactory, time);
        ToolApprovalService approvals = new(models, store);
        media.ArchiveCompleted += models.NotifyMediaChanged;
        AgentAuthorization authorization = new(store, store, profiles);
        OutgoingPipeline pipeline = new(profiles, outgoing, approvals, store, authorization, toolScheduler, pluginExecution.ToolsAndProcessors.PerPluginConcurrency, time);
        MessageSender sender = new(store, store, pipeline, transports ?? [], signal, runId, loggerFactory.CreateLogger<MessageSender>(), time, adapters);
        AgentToolExecutor toolExecutor = new(store, store, capabilities, profiles, approvals, authorization, sender, toolScheduler, agentSettings, pluginExecution, time);
        AgentRuntime agents = new(store, store, store, store, store, store, models, modelProviders, media, contents, sender, profiles, capabilities, toolExecutor, agentSettings, pluginExecution, runId, time);
        FairInvocationScheduler scheduler = new(pluginExecution.Handlers.Concurrency, pluginExecution.Handlers.QueueCapacity);
        PluginManager plugins = new(options, new PluginHostServices
        {
            History = store,
            Events = store,
            Ingress = ingress,
            EventIngress = ingress,
            Adapters = adapters,
            ModelProviders = modelProviders,
            Handlers = handlers,
            Tools = tools,
            Agents = agents,
            AgentHistory = store,
            OutgoingHistory = store,
            OutgoingProcessors = outgoing,
            ConfigurationValidators = validators,
            Models = models,
            ModelCatalog = profiles,
            Profiles = profiles,
            Receipts = sender,
            Media = media,
            MediaImporter = media,
            Contents = contents,
            Transcriptions = store,
            Sender = sender,
            LoggerFactory = loggerFactory
        }, pluginExecution, time);
        MessageDispatcher dispatcher = new(handlers, orders, scheduler, sender, loggerFactory.CreateLogger<MessageDispatcher>());
        InfrastructureRuntime runtime = new(store, media, dispatcher, plugins, scheduler, orders, sender, signal, runId,
            options, loggerFactory.CreateLogger<InfrastructureRuntime>(), time, adapters, contents, modelProviders, models, profiles, handlers, capabilities, approvals, toolScheduler, toolExecutor, agents);
        ingress.Bind(runtime);
        return runtime;
    }
}
