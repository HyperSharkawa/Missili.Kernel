using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>基础设施容量和期限；这些是初始默认值，不是吞吐量承诺。</summary>
public sealed class InfrastructureOptions
{
    public string DataDirectory { get; set; } = "data";
    /// <summary>共享会话配置目录；未指定时不启用配置文件读取与监视。</summary>
    public string? ConversationConfigurationDirectory { get; set; }
    public int IngressCapacity { get; set; } = 512;
    public int MessageConcurrency { get; set; } = 32;
    public int WriteCapacity { get; set; } = 256;
    public int ReadConcurrency { get; set; } = 4;
    public int DownloadConcurrency { get; set; } = 4;
    public int DownloadCapacity { get; set; } = 128;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PluginStopTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int ContentConcurrency { get; set; } = 2;
    public int ContentCapacity { get; set; } = 64;

    /// <summary>在启动后台工作前拒绝非法配置，不静默修正容量。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        if (ConversationConfigurationDirectory is not null) ArgumentException.ThrowIfNullOrWhiteSpace(ConversationConfigurationDirectory);
        int[] capacities = [IngressCapacity, MessageConcurrency, WriteCapacity, ReadConcurrency, DownloadConcurrency, DownloadCapacity, ContentConcurrency, ContentCapacity];
        if (capacities.Any(value => value <= 0)) throw new ArgumentException("队列容量和并发数必须大于零。");
        if (DownloadCapacity < DownloadConcurrency) throw new ArgumentException("媒体队列容量不能小于下载并发数。");
        if (ContentCapacity < ContentConcurrency) throw new ArgumentException("结构化内容队列容量不能小于归档并发数。");
        TimeSpan[] timeouts = [ConnectTimeout, HeadersTimeout, ReadIdleTimeout, PluginStopTimeout];
        if (timeouts.Any(value => value <= TimeSpan.Zero)) throw new ArgumentException("超时必须大于零。");
    }
}
