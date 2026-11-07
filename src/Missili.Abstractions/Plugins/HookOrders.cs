using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Missili.Abstractions;

/// <summary>当前顺序中的一个处理器和新增标记。</summary>
public sealed record HookOrderItem
{
    public required HandlerDescriptor Handler { get; init; }
    public bool IsNew { get; init; }
}

/// <summary>钩子编排视图，供未来管理界面直接使用。</summary>
public sealed record HookOrderView
{
    public required string HookId { get; init; }
    public required HandlerKind Kind { get; init; }
    public required long Revision { get; init; }
    public required bool Customized { get; init; }
    public required ImmutableArray<HookOrderItem> Items { get; init; }
}

/// <summary>用户编排服务，使用修订号避免互相覆盖。</summary>
public interface IHookOrderService
{
    /// <summary>读取当前有效顺序，保留临时停用插件的已保存位置。</summary>
    Task<HookOrderView> GetAsync(string hookId, HandlerKind kind, CancellationToken cancellationToken = default);
    /// <summary>保存当前处理器排序；持久化失败时不发布新顺序。</summary>
    Task<HookOrderView> SaveAsync(string hookId, HandlerKind kind, ImmutableArray<HandlerKey> order, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>恢复插件默认顺序并增加修订号。</summary>
    Task<HookOrderView> ResetAsync(string hookId, HandlerKind kind, long expectedRevision, CancellationToken cancellationToken = default);
}
