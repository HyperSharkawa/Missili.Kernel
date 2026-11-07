using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>内容展开状态独立于其中媒体的归档状态。</summary>
public enum ContentState { Pending, Expanding, Complete, Partial, Failed, Limited, WaitingForAdapter }

/// <summary>可持久化的转发或引用查询描述，不包含任何插件类型。</summary>
public sealed record ContentSource
{
    public required string Kind { get; init; }
    public int Version { get; init; } = 1;
    public required MessageAddress Address { get; init; }
    public required string PlatformId { get; init; }
    /// <summary>校验身份与版本，不依据展示文本猜测来源。</summary>
    public void Validate()
    {
        Address.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(PlatformId);
        if (Version != 1) throw new ArgumentException("内容来源版本不受支持。");
    }
    /// <summary>复制纯值来源，供排队和持久化使用。</summary>
    public ContentSource Freeze() => this with { };
}

/// <summary>展开中的局部问题，位置稳定且不代替原始节点。</summary>
public sealed record ContentIssue
{
    public required string Path { get; init; }
    public required string Code { get; init; }
    public required string Description { get; init; }
}

/// <summary>解析器提交的一个节点，媒体尚未取得宿主引用。</summary>
public sealed record IncomingContentNode
{
    public required string Path { get; init; }
    public string? SenderId { get; init; }
    public string? SenderName { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public JsonElement? Metadata { get; init; }
    public ImmutableArray<IncomingSegment> Segments { get; init; } = [];
    public ImmutableArray<IncomingContentNode> Children { get; init; } = [];
}

/// <summary>持久化后的节点快照，不进入普通消息分发。</summary>
public sealed record ContentNode
{
    public required string Path { get; init; }
    public string? SenderId { get; init; }
    public string? SenderName { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public JsonElement? Metadata { get; init; }
    public ImmutableArray<MessageSegment> Segments { get; init; } = [];
    public ImmutableArray<ContentNode> Children { get; init; } = [];
}

/// <summary>归档的当前内容与完整性信息，失败可以包含成功的分支。</summary>
public sealed record StructuredContentSnapshot
{
    public required Guid Id { get; init; }
    public required ContentSource Source { get; init; }
    public ContentState State { get; init; }
    public ImmutableArray<ContentNode> Nodes { get; init; } = [];
    public ImmutableArray<ContentIssue> Issues { get; init; } = [];
    public JsonElement? Checkpoint { get; init; }
    public long Revision { get; init; }
}

/// <summary>解析器的一次进度提交，保存后才继续后续远端请求。</summary>
public sealed record ContentExpansion
{
    public ContentState State { get; init; }
    public ImmutableArray<IncomingContentNode> Nodes { get; init; } = [];
    public ImmutableArray<ContentIssue> Issues { get; init; } = [];
    public JsonElement? Checkpoint { get; init; }
}

/// <summary>读取或显式请求引用补全，调用者取消只结束自身等待。</summary>
public interface IStructuredContentStore
{
    /// <summary>读取已保存状态，不触发失败任务重试。</summary>
    Task<StructuredContentSnapshot?> GetContentAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>等待已有任务结束，返回部分成功或失败信息。</summary>
    Task<StructuredContentSnapshot> WaitContentAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>显式登记引用或转发获取，返回持久化身份。</summary>
    Task<Guid> RequestContentAsync(ContentSource source, CancellationToken cancellationToken = default);
}
