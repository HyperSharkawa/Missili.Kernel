using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>排序配置采用短事务和修订号，保存失败不会发布半份配置。</summary>
public sealed partial class SqliteStore
{
    /// <summary>不存在配置时返回修订号零与默认排序模式。</summary>
    public Task<StoredHookOrder> GetHookOrderAsync(string hookId, HandlerKind kind, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        HookOrderEntity? row = database.HookOrders.AsNoTracking().Include(item => item.Entries).SingleOrDefault(item => item.HookId == hookId && item.Kind == kind);
        return row is null ? new StoredHookOrder() : OrderSnapshot(row);
    }, cancellationToken);

    /// <summary>在同一事务内替换顺序，临时停用插件的位置由上层合并后保留。</summary>
    public Task<StoredHookOrder> SaveHookOrderAsync(string hookId, HandlerKind kind, ImmutableArray<HandlerKey> entries, bool customized, long expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hookId);
        if (!Enum.IsDefined(kind) || entries.IsDefault || entries.Distinct().Count() != entries.Length)
            throw new ArgumentException("钩子顺序包含非法类型或重复标识。");
        return WriteAsync(database =>
        {
            using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = database.Database.BeginTransaction();
            HookOrderEntity? row = database.HookOrders.SingleOrDefault(item => item.HookId == hookId && item.Kind == kind);
            if ((row?.Revision ?? 0) != expectedRevision) throw new RevisionConflictException();
            if (row is null)
            {
                row = new HookOrderEntity { HookId = hookId, Kind = kind };
                database.HookOrders.Add(row);
            }
            else
            {
                // 先删除旧位置但不提交事务，避免交换顺序时触发位置唯一约束。
                database.HookOrderEntries.Where(item => item.HookId == hookId && item.Kind == kind).ExecuteDelete();
            }
            row.Revision++;
            row.Customized = customized;
            for (int index = 0; index < entries.Length; index++)
                row.Entries.Add(new HookEntryEntity { HookId = hookId, Kind = kind, PluginId = entries[index].PluginId, HandlerId = entries[index].HandlerId, Position = index });
            try
            {
                database.SaveChanges();
                transaction.Commit();
            }
            catch (DbUpdateConcurrencyException) { throw new RevisionConflictException(); }
            return OrderSnapshot(row);
        }, cancellationToken);
    }

    /// <summary>读取持久化位置，不捕获任何运行时处理器引用。</summary>
    private static StoredHookOrder OrderSnapshot(HookOrderEntity row) => new()
    {
        Revision = row.Revision,
        Customized = row.Customized,
        Entries = row.Entries.OrderBy(item => item.Position).Select(item => new HandlerKey { PluginId = item.PluginId, HandlerId = item.HandlerId }).ToImmutableArray()
    };
}
