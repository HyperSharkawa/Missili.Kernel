using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>模型模块的实体定义不依赖插件或模型 SDK。</summary>
public sealed partial class MissiliDbContext
{
    public DbSet<ConversationWindowEntity> ConversationWindows => Set<ConversationWindowEntity>();
    public DbSet<ModelCallEntity> ModelCalls => Set<ModelCallEntity>();
    public DbSet<ModelAttemptEntity> ModelAttempts => Set<ModelAttemptEntity>();
    public DbSet<MediaRepresentationEntity> MediaRepresentations => Set<MediaRepresentationEntity>();

    /// <summary>窗口、调用及派生文件使用独立职责的索引，不创建转述任务表。</summary>
    private static void ConfigureModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationWindowEntity>(entity =>
        {
            entity.ToTable("ConversationWindows"); entity.HasKey(item => item.ConversationKey);
            entity.Property(item => item.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<ModelCallEntity>(entity =>
        {
            entity.ToTable("ModelCalls"); entity.HasKey(item => item.Sequence); entity.HasAlternateKey(item => item.Id);
            entity.HasIndex(item => new { item.StartedAt, item.Sequence });
            entity.HasIndex(item => new { item.ConversationKey, item.Sequence });
            entity.HasIndex(item => new { item.Purpose, item.Sequence });
            entity.HasIndex(item => new { item.State, item.Sequence });
            entity.HasIndex(item => new { item.RunId, item.State });
            entity.HasMany(item => item.Attempts).WithOne(item => item.Call).HasForeignKey(item => item.CallId).HasPrincipalKey(item => item.Id).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ModelAttemptEntity>(entity =>
        {
            entity.ToTable("ModelAttempts"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.CallId, item.Sequence }).IsUnique();
        });
        modelBuilder.Entity<MediaRepresentationEntity>(entity =>
        {
            entity.ToTable("MediaRepresentations"); entity.HasKey(item => new { item.ContentKey, item.Kind });
            entity.HasOne<BlobEntity>().WithMany().HasForeignKey(item => item.ContentKey).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MediaEntity>().WithMany().HasForeignKey(item => item.MediaId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>只保存已淘汰边界，不复制历史或创建摘要空壳。</summary>
public sealed class ConversationWindowEntity
{
    public string ConversationKey { get; set; } = "";
    public long DiscardedThrough { get; set; }
    public long Revision { get; set; }
}

/// <summary>逻辑调用身份、配置修订与结束状态，不用调用记录恢复执行。</summary>
public sealed class ModelCallEntity
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Purpose { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long ConfigurationRevision { get; set; }
    public string? ConversationKey { get; set; }
    public long? TriggerMessageId { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
    public ModelCallState State { get; set; }
    public string? ModelId { get; set; }
    public string? Error { get; set; }
    public List<ModelAttemptEntity> Attempts { get; set; } = [];
}

/// <summary>每次候选实际尝试独立保存用量、错误及详情标识。</summary>
public sealed class ModelAttemptEntity
{
    public Guid Id { get; set; }
    public Guid CallId { get; set; }
    public ModelCallEntity Call { get; set; } = null!;
    public string ModelId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public Guid? Generation { get; set; }
    public int Round { get; set; }
    public int Sequence { get; set; }
    public long StartedAt { get; set; }
    public long? FinishedAt { get; set; }
    public ModelCallState State { get; set; }
    public string? Error { get; set; }
    public string? ErrorKind { get; set; }
    public int? HttpStatus { get; set; }
    public string? RequestId { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public bool HasPartialResult { get; set; }
    public string? DetailId { get; set; }
}

/// <summary>原内容键到可复用派生格式的关联，派生文件不替代原件身份。</summary>
public sealed class MediaRepresentationEntity
{
    public string ContentKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid MediaId { get; set; }
}
