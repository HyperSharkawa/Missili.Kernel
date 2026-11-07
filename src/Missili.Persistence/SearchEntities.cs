using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>检索投影只保存明确可展示字段，FTS 索引由同一事务的 SQLite 触发器维护。</summary>
public sealed partial class MissiliDbContext
{
    public DbSet<SearchDocumentEntity> SearchDocuments => Set<SearchDocumentEntity>();

    /// <summary>来源唯一键保证更新不会产生重复搜索命中。</summary>
    private static void ConfigureSearch(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SearchDocumentEntity>(entity =>
        {
            entity.ToTable("SearchDocuments"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.Kind, item.SourceId }).IsUnique();
            entity.HasIndex(item => new { item.ConversationKey, item.At, item.Id });
            entity.HasIndex(item => new { item.TaskId, item.At, item.Id });
            entity.HasIndex(item => item.MessageId);
        });
    }
}

/// <summary>源文本与统一大小写投影分开，原文不会被改写成归一化文本。</summary>
public sealed class SearchDocumentEntity
{
    public long Id { get; set; }
    public HistoryRecordKind Kind { get; set; }
    public string SourceId { get; set; } = "";
    public string ConversationKey { get; set; } = "";
    public long At { get; set; }
    public string? SenderId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? AgentId { get; set; }
    public long? MessageId { get; set; }
    public string Text { get; set; } = "";
    public string NormalizedText { get; set; } = "";
    public bool Hidden { get; set; }
}

/// <summary>SQL 写入和查询复用同一 Unicode 大小写规则与地址序列化，避免平台或语言差异。</summary>
internal static class SearchValues
{
    /// <summary>只归一化检索副本，保持源文本原貌。</summary>
    public static string Fold(string text) => text.ToUpperInvariant();
    /// <summary>SQLite JSON 和宿主 JSON 的字符转义可以不同，统一转换为宿主会话键。</summary>
    public static string Address(string json) => JsonSerializer.Serialize(JsonSerializer.Deserialize<MessageAddress>(json)!);
}
