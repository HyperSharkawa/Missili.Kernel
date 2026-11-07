using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>可回收 OneBot 插件入口；网络任务在全部配置校验与注册发布后启动。</summary>
public sealed class Entry : IPlugin
{
    private readonly List<OneBotAdapter> _adapters = [];

    /// <summary>使用私有源生成上下文解析配置，登记驱动、任务和资源的代次归属。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        PluginOptions configuration = context.Configuration.Deserialize(OneBotJsonContext.Default.PluginOptions) ?? throw new InvalidDataException("OneBot 配置为空。");
        if (configuration.Instances.Length == 0 || configuration.Instances.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != configuration.Instances.Length)
            throw new ArgumentException("OneBot 配置必须包含不重复的实例。");
        string?[] tokens = configuration.Instances.Select(item => item.ValidateAndGetToken()).ToArray();
        for (int index = 0; index < configuration.Instances.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OneBotOptions options = configuration.Instances[index];
            OneBotAdapter adapter = new(options, tokens[index], context);
            _adapters.Add(adapter);
            context.Adapters.Register(new AdapterRegistration
            {
                AdapterId = options.Id,
                BotAccountId = options.BotAccountId,
                PlatformId = "qq",
                ReverseWebSocket = options.Direction == ConnectionDirection.Reverse,
                Driver = adapter
            });
            context.Tasks.Track(adapter);
            _ = context.Tasks.RunAfterActivation(adapter.RunAsync);
        }
        return Task.CompletedTask;
    }

    /// <summary>立即请求连接与工作停止，Nexus 等待登记任务和调用实际退出后才卸载。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    { foreach (OneBotAdapter adapter in _adapters) await adapter.StopAsync().ConfigureAwait(false); }
}
