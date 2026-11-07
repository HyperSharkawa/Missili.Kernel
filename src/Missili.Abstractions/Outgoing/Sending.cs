using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>统一发送意图，实际平台编码由后续传输实现承担。</summary>
public sealed record OutgoingMessage
{
    public Guid? BatchId { get; init; }
    public int? BatchPosition { get; init; }
    public required MessageAddress Address { get; init; }
    public required ImmutableArray<MessageSegment> Segments { get; init; }
    public JsonElement? Metadata { get; init; }

    /// <summary>复制 JSON 所有权，发送队列不引用调用者的文档。</summary>
    public OutgoingMessage Freeze() => this with { Metadata = Metadata?.Clone(), Segments = Segments.Select(item => item.Freeze()).ToImmutableArray() };
}

/// <summary>平台调用结果，未知状态不自动重发。</summary>
public sealed record TransportResult
{
    public required DeliveryState State { get; init; }
    public string? PlatformMessageId { get; init; }
    public string? Error { get; init; }
}

/// <summary>宿主发送结果，同时表达平台结果与结果记录是否成功。</summary>
public sealed record SendResult
{
    public long? MessageId { get; init; }
    public required DeliveryState State { get; init; }
    public bool Recorded { get; init; }
    public string? Error { get; init; }
}

/// <summary>固定平台传输边界；动态适配器经加载代次路由接入同一发送服务。</summary>
public interface IMessageTransport
{
    string AdapterId { get; }
    /// <summary>执行一次传输请求；方法本身不得自动重试。</summary>
    Task<TransportResult> SendAsync(long messageId, OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>将发送意图、外部调用与持久化结果统一管理。</summary>
public interface IMessageSender
{
    /// <summary>保存意图后调用传输一次；返回明确结果及记录状态。</summary>
    Task<BatchSendResult> SendAsync(OutgoingBatch batch, CancellationToken cancellationToken = default);
}
