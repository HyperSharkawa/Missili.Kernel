using Missili.Abstractions;
using Missili.Kernel;
using Missili.Matrix;
using Missili.Nexus;
using Missili.Persistence;

namespace Missili.Host;

/// <summary>唯一的基础设施组装位置，运行项目之间保持契约依赖。</summary>
public static class InfrastructureFactory
{
    /// <summary>创建拥有完整资源生命周期的运行时；传输集合为空即没有平台接入。</summary>
    public static InfrastructureRuntime Create(InfrastructureOptions options, ILoggerFactory loggerFactory,
        IEnumerable<IMessageTransport>? transports = null, TimeProvider? clock = null)
    {
        options.Validate();
        TimeProvider time = clock ?? TimeProvider.System;
        Guid runId = Guid.NewGuid();
        SqliteStore store = new(Path.Combine(options.DataDirectory, "history.db"), options);
        DispatchSignal signal = new();
        MediaArchive media = new(store, options, loggerFactory.CreateLogger<MediaArchive>(), time);
        MessageSender sender = new(store, transports ?? [], signal, runId, loggerFactory.CreateLogger<MessageSender>(), time);
        FairInvocationScheduler scheduler = new(options.PluginConcurrency, options.InvocationCapacity);
        PluginManager plugins = new(options, new PluginHostServices
        { History = store, Media = media, Transcriptions = store, Sender = sender, LoggerFactory = loggerFactory }, time);
        HookOrderService orders = new(store, plugins);
        MessageDispatcher dispatcher = new(plugins, orders, scheduler, sender, loggerFactory.CreateLogger<MessageDispatcher>());
        return new InfrastructureRuntime(store, media, dispatcher, plugins, scheduler, orders, sender, signal, runId,
            options, loggerFactory.CreateLogger<InfrastructureRuntime>(), time);
    }
}
