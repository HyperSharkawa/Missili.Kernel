using System.Text.Json;
using Missili.Abstractions;
using Missili.Kernel;

namespace Missili.Host;

/// <summary>按宿主生命周期启动基础设施和明确启用的本地编译插件。</summary>
internal sealed class InfrastructureLifetime : IHostedLifecycleService
{
    private readonly InfrastructureRuntime _runtime;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger _logger;

    /// <summary>只接收宿主基础设施，不向插件暴露配置根或服务容器。</summary>
    public InfrastructureLifetime(InfrastructureRuntime runtime, IConfiguration configuration, IHostEnvironment environment, ILogger<InfrastructureLifetime> logger)
    { _runtime = runtime; _configuration = configuration; _environment = environment; _logger = logger; }

    /// <summary>基础设施完成迁移后再加载启用项，单插件失败不阻止其他插件。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _runtime.StartAsync(cancellationToken);
        foreach (IConfigurationSection item in _configuration.GetSection("Plugins").GetChildren())
        {
            if (!item.GetValue("Enabled", false)) continue;
            try
            {
                string directory = item["Directory"] ?? throw new ArgumentException("启用插件必须提供 Directory。");
                string? configurationFile = item["ConfigurationFile"];
                string json = configurationFile is null ? "{}" : await File.ReadAllTextAsync(
                    Path.GetFullPath(configurationFile, _environment.ContentRootPath), cancellationToken);
                using JsonDocument document = JsonDocument.Parse(json);
                PluginStatus status = await _runtime.Plugins.LoadAsync(Path.GetFullPath(directory, _environment.ContentRootPath), document.RootElement, cancellationToken);
                if (status.State != PluginState.Active) _logger.LogError("插件 {PluginId} 未就绪：{Error}", status.Id, status.Error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "已启用插件加载失败，继续启动其他插件"); }
        }
        _runtime.Models.StartBackgroundScan();
    }

    /// <summary>请求有序停止，存储关闭由运行时实际工作退出后执行。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);
    /// <summary>启动前阶段不提前连接数据库或加载插件。</summary>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>启动工作已经由 StartAsync 顺序完成。</summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <summary>在 Kestrel 等待活动请求退出之前关闭插件连接，避免双方互等。</summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);
    /// <summary>资源最终释放由运行时异步释放入口负责。</summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
