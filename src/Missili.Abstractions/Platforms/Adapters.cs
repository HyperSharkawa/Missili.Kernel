using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>适配器连接状态；与 Nexus 的插件启停状态分别表达。</summary>
public sealed record AdapterStatus
{
    public required string AdapterId { get; init; }
    public required string BotAccountId { get; init; }
    public required string PlatformId { get; init; }
    public Guid Generation { get; init; }
    public bool Connected { get; init; }
    public bool ApiAvailable { get; init; }
    public bool EventsAvailable { get; init; }
    public bool IdentityVerified { get; init; }
    public bool? Online { get; init; }
}

/// <summary>查询和平台操作的确定性，受理与确认成功不能混淆。</summary>
public enum PlatformOperationState { Succeeded, Failed, Accepted, Unknown, Unavailable, Cancelled }

/// <summary>操作回执保留协议状态与错误码，载荷仅包含操作结果。</summary>
public sealed record PlatformOperationResult
{
    public PlatformOperationState State { get; init; }
    public string? Status { get; init; }
    public int? Retcode { get; init; }
    public string? Error { get; init; }
    public JsonElement? Data { get; init; }
    /// <summary>复制回执数据，调用结束后不依赖接收缓冲或文档。</summary>
    public PlatformOperationResult Freeze() => this with { Data = Data?.Clone() };
}

/// <summary>宿主持有的升级请求；实现不得在连接结束前返回 HTTP 请求。</summary>
public interface IWebSocketRequest
{
    bool IsWebSocketRequest { get; }
    ImmutableDictionary<string, string> Headers { get; }
    /// <summary>完成升级后返回连接，由处理调用持有到关闭。</summary>
    Task<WebSocket> AcceptAsync(TimeSpan keepAliveInterval, TimeSpan keepAliveTimeout, CancellationToken cancellationToken);
    /// <summary>升级前拒绝无效身份、角色或鉴权，不回显凭据。</summary>
    Task RejectAsync(int statusCode, CancellationToken cancellationToken);
}

/// <summary>媒体解析得到流或宿主 HTTP 来源，二者必须且只能提供一个。</summary>
public sealed class MediaSourceRead : IAsyncDisposable
{
    public Stream? Content { get; init; }
    public MediaSource? Redirect { get; init; }
    public JsonElement? Representation { get; init; }
    /// <summary>释放解析期间持有的流和对应插件调用资格。</summary>
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>实际平台驱动。该对象只由所属插件代次登记和持有。</summary>
public interface IAdapterDriver
{
    /// <summary>读取纯值连接状态，不能返回 Token 或本地路径。</summary>
    AdapterStatus GetStatus();
    /// <summary>执行一次发送，不跨连接自动重发。</summary>
    Task<TransportResult> SendAsync(long messageId, OutgoingMessage message, CancellationToken cancellationToken);
    /// <summary>执行非发送平台操作；公共入口禁止绕过发送意图。</summary>
    Task<PlatformOperationResult> InvokeAsync(string action, JsonElement parameters, CancellationToken cancellationToken);
    /// <summary>验证并处理一条反向连接，请求直到网络任务退出才结束。</summary>
    Task AcceptAsync(IWebSocketRequest request, CancellationToken cancellationToken);
    /// <summary>解析平台媒体，返回原件流或宿主可继续下载的 HTTP 来源。</summary>
    Task<MediaSourceRead> ResolveMediaAsync(MediaSource source, CancellationToken cancellationToken);
    /// <summary>显式请求媒体派生格式，返回可读取流，不把重命名当作转码。</summary>
    Task<MediaSourceRead> ResolveRepresentationAsync(MediaSource source, string format, CancellationToken cancellationToken);
    /// <summary>展开结构化内容，每次成功进度通过宿主回调持久化后继续。</summary>
    Task ExpandContentAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken cancellationToken);
}

/// <summary>稳定适配器身份和当前实现，不持久化驱动对象。</summary>
public sealed record AdapterRegistration
{
    public required string AdapterId { get; init; }
    public required string BotAccountId { get; init; }
    public required string PlatformId { get; init; }
    public bool ReverseWebSocket { get; init; }
    public required IAdapterDriver Driver { get; init; }
}

/// <summary>初始化暂存注册，成功后随代次统一发布。</summary>
public interface IAdapterRegistry
{
    /// <summary>登记唯一身份，不能在初始化结束后增加新的适配器。</summary>
    void Register(AdapterRegistration registration);
    /// <summary>连接可用性变化时唤醒等待归档；不改变插件生命周期。</summary>
    void NotifyChanged();
}

/// <summary>插件可调用的非发送平台操作，不占消息处理器调度额度。</summary>
public interface IPlatformOperations
{
    /// <summary>读取已发布适配器的纯值状态。</summary>
    ImmutableArray<AdapterStatus> GetStatuses();
    /// <summary>执行明确的账号操作，离线立即返回不可用。</summary>
    Task<PlatformOperationResult> InvokeAsync(string adapterId, string botAccountId, string action, JsonElement parameters, CancellationToken cancellationToken = default);
}

/// <summary>适配器暂不可用或被重载，宿主保留可恢复归档。</summary>
public sealed class AdapterUnavailableException : InvalidOperationException
{
    /// <summary>创建不包含账号配置和网络地址的诊断。</summary>
    public AdapterUnavailableException() : base("适配器暂不可用，等待适配器恢复。") { }
}

/// <summary>可直接展示的原件获取失败，只允许实现提供不包含地址或凭据的说明。</summary>
public sealed class MediaAcquisitionException : IOException
{
    /// <summary>保存明确失败原因，供历史说明读取。</summary>
    public MediaAcquisitionException(string message) : base(message) { }
}

/// <summary>迟到发送回执走宿主记录，不再次调用网络或重复通知。</summary>
public interface ISendReceiptSink
{
    /// <summary>记录属于本实例的已取得回执，并报告记录是否成功。</summary>
    Task<SendResult> RecordReceiptAsync(long messageId, TransportResult result, CancellationToken cancellationToken = default);
}
