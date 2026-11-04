using Missili.Abstractions;
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
        IEnumerable<IMessageTransport>? transports = null, TimeProvider? clock = null, ModelOptions? modelOptions = null)
    {
        options.Validate();
        TimeProvider time = clock ?? TimeProvider.System;
        Guid runId = Guid.NewGuid();
        SqliteStore store = new(Path.Combine(options.DataDirectory, "history.db"), options);
        DispatchSignal signal = new();
        AdapterDirectory adapters = new();
        ModelProviderDirectory modelProviders = new();
        IngressRouter ingress = new();
        MediaArchive media = new(store, options, loggerFactory.CreateLogger<MediaArchive>(), time, adapters);
        StructuredContentArchive contents = new(store, adapters, media, options, loggerFactory.CreateLogger<StructuredContentArchive>(), time);
        ModelRuntime models = new(store, store, media, media, adapters, contents, modelProviders, modelOptions ?? new ModelOptions(), options.DataDirectory, runId, loggerFactory, time);
        media.ArchiveCompleted += models.NotifyMediaChanged;
        MessageSender sender = new(store, transports ?? [], signal, runId, loggerFactory.CreateLogger<MessageSender>(), time, adapters);
        FairInvocationScheduler scheduler = new(options.PluginConcurrency, options.InvocationCapacity);
        PluginManager plugins = new(options, new PluginHostServices
        {
            History = store,
            Events = store,
            Ingress = ingress,
            EventIngress = ingress,
            Adapters = adapters,
            ModelProviders = modelProviders,
            Models = models,
            Receipts = sender,
            Media = media,
            MediaImporter = media,
            Contents = contents,
            Transcriptions = store,
            Sender = sender,
            LoggerFactory = loggerFactory
        }, time);
        HookOrderService orders = new(store, plugins);
        MessageDispatcher dispatcher = new(plugins, orders, scheduler, sender, loggerFactory.CreateLogger<MessageDispatcher>());
        InfrastructureRuntime runtime = new(store, media, dispatcher, plugins, scheduler, orders, sender, signal, runId,
            options, loggerFactory.CreateLogger<InfrastructureRuntime>(), time, adapters, contents, modelProviders, models);
        ingress.Bind(runtime);
        return runtime;
    }
}
