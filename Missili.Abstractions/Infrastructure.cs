using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>基础设施容量和期限；这些是初始默认值，不是吞吐量承诺。</summary>
public sealed class InfrastructureOptions
{
    public string DataDirectory { get; set; } = "data";
    public int IngressCapacity { get; set; } = 512;
    public int MessageConcurrency { get; set; } = 32;
    public int PluginConcurrency { get; set; } = 32;
    public int PerPluginConcurrency { get; set; } = 4;
    public int InvocationCapacity { get; set; } = 512;
    public int WriteCapacity { get; set; } = 256;
    public int ReadConcurrency { get; set; } = 4;
    public int DownloadConcurrency { get; set; } = 4;
    public int DownloadCapacity { get; set; } = 128;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PluginStopTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>在启动后台工作前拒绝非法配置，不静默修正容量。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        int[] capacities = [IngressCapacity, MessageConcurrency, PluginConcurrency, PerPluginConcurrency,
            InvocationCapacity, WriteCapacity, ReadConcurrency, DownloadConcurrency, DownloadCapacity];
        if (capacities.Any(value => value <= 0)) throw new ArgumentException("队列容量和并发数必须大于零。");
        if (PerPluginConcurrency > PluginConcurrency) throw new ArgumentException("单插件并发不能超过全局并发。");
        if (DownloadCapacity < DownloadConcurrency) throw new ArgumentException("媒体队列容量不能小于下载并发数。");
        TimeSpan[] timeouts = [ConnectTimeout, HeadersTimeout, ReadIdleTimeout, PluginStopTimeout];
        if (timeouts.Any(value => value <= TimeSpan.Zero)) throw new ArgumentException("超时必须大于零。");
    }
}

/// <summary>媒体持久化的内部工作记录，不作为插件可见 DTO。</summary>
public sealed record MediaWork
{
    public required MediaStatus Status { get; init; }
    public required Uri Source { get; init; }
    public string? SourceKey { get; init; }
    public string? TemporaryPath { get; init; }
    public string? RelativePath { get; init; }
}

/// <summary>持久化的顺序配置，仅包含稳定标识。</summary>
public sealed record StoredHookOrder
{
    public long Revision { get; init; }
    public bool Customized { get; init; }
    public ImmutableArray<HandlerKey> Entries { get; init; } = [];
}

/// <summary>宿主实现之间的存储边界，插件上下文只暴露其读取子接口。</summary>
public interface IInfrastructureStore : IMessageHistory, IMediaTranscriptionStore, IAsyncDisposable
{
    /// <summary>应用迁移并标记上次中断业务，恢复未完成的媒体状态。</summary>
    Task InitializeAsync(Guid runId, CancellationToken cancellationToken);
    /// <summary>通过实际数据库查询验证存储可读取。</summary>
    Task CheckHealthAsync(CancellationToken cancellationToken);
    /// <summary>在一个事务中追加收到的消息及媒体引用。</summary>
    Task<ReceiveReceipt> AppendAsync(IncomingMessage message, DateTimeOffset receivedAt, Guid runId, CancellationToken cancellationToken);
    /// <summary>记录发送意图，返回其库内消息标识。</summary>
    Task<MessageSnapshot> AppendOutgoingAsync(OutgoingMessage message, DateTimeOffset now, Guid runId, CancellationToken cancellationToken);
    /// <summary>保存一次发送结果，确认成功时生成发送后分发状态。</summary>
    Task SetDeliveryAsync(long id, TransportResult result, CancellationToken cancellationToken);
    /// <summary>批量领取本次运行尚未分发的消息，并标记运行中。</summary>
    Task<ImmutableArray<MessageSnapshot>> ClaimMessagesAsync(Guid runId, int count, CancellationToken cancellationToken);
    /// <summary>保存业务结束或中断状态。</summary>
    Task SetProcessingAsync(long id, ProcessingState state, CancellationToken cancellationToken);
    /// <summary>宿主停止时标记未完成业务，禁止下次启动自动重放。</summary>
    Task InterruptRunAsync(Guid runId, CancellationToken cancellationToken);
    /// <summary>更新撤回和删除标记，保留原始内容。</summary>
    Task SetVisibilityAsync(long id, bool recalled, bool deleted, CancellationToken cancellationToken);
    /// <summary>领取有限批次归档工作，包含需要收尾的文件操作。</summary>
    Task<ImmutableArray<MediaWork>> ClaimMediaAsync(int count, CancellationToken cancellationToken);
    /// <summary>取得媒体内部状态。</summary>
    Task<MediaWork?> GetMediaAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>查找稳定来源键对应的已归档原件。</summary>
    Task<MediaWork?> FindReusableMediaAsync(string sourceKey, CancellationToken cancellationToken);
    /// <summary>登记文件提交所需信息，使进程中断后可以恢复。</summary>
    Task SetMediaFinalizingAsync(Guid id, string contentKey, long length, string temporaryPath, string relativePath, CancellationToken cancellationToken);
    /// <summary>提交可用原件与当前引用关联。</summary>
    Task CompleteMediaAsync(Guid id, string contentKey, long length, string relativePath, CancellationToken cancellationToken);
    /// <summary>记录失败而不删除原始消息。</summary>
    Task FailMediaAsync(Guid id, string error, CancellationToken cancellationToken);
    /// <summary>读取某钩子与执行阶段的持久化顺序。</summary>
    Task<StoredHookOrder> GetHookOrderAsync(string hookId, HandlerKind kind, CancellationToken cancellationToken);
    /// <summary>按修订号保存配置，不允许旧版本覆盖新版本。</summary>
    Task<StoredHookOrder> SaveHookOrderAsync(string hookId, HandlerKind kind, ImmutableArray<HandlerKey> entries, bool customized, long expectedRevision, CancellationToken cancellationToken);
}

/// <summary>公平调度契约；入队与实际完成分开，调用资格仍由 Nexus 管理。</summary>
public interface IInvocationScheduler : IAsyncDisposable
{
    /// <summary>等待有限入队容量后返回完成任务；等待插件额度不占活动槽。</summary>
    Task<Task<T>> EnqueueAsync<T>(string pluginId, int maxConcurrency, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
