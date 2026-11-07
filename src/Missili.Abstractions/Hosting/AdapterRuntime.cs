using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>宿主内部路由边界，调用资格仍归属 Nexus。</summary>
public interface IAdapterRuntime : IPlatformOperations
{
    /// <summary>经已登记代次发送，连接不可用时不等待重连。</summary>
    Task<TransportResult> SendAsync(long id, OutgoingMessage message, CancellationToken cancellationToken);
    /// <summary>路由固定反向入口，未注册或停用实例返回明确拒绝。</summary>
    Task AcceptAsync(string adapterId, IWebSocketRequest request, CancellationToken cancellationToken);
    /// <summary>解析媒体并让返回的流继续持有代次资格。</summary>
    Task<MediaSourceRead> ResolveMediaAsync(MediaSource source, CancellationToken cancellationToken);
    /// <summary>派生格式读取同样持有适配器代次资格直到流被释放。</summary>
    Task<MediaSourceRead> ResolveRepresentationAsync(MediaSource source, string format, CancellationToken cancellationToken);
    /// <summary>在归档工作内调用当前代次，调用取消不会重放聊天业务。</summary>
    Task ExpandContentAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken cancellationToken);
    /// <summary>等待适配器目录发生变化，避免等待归档忙轮询。</summary>
    Task WaitForChangeAsync(long revision, CancellationToken cancellationToken);
    long Revision { get; }
}
