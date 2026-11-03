using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>宿主专用 EF 上下文，生命周期限定于单个存储操作。</summary>
public sealed class MissiliDbContext : DbContext
{
    /// <summary>使用外部固定的 SQLite 选项创建上下文，不持有插件类型。</summary>
    public MissiliDbContext(DbContextOptions<MissiliDbContext> options) : base(options) { }

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<SegmentEntity> MessageSegments => Set<SegmentEntity>();
    public DbSet<MediaEntity> MediaReferences => Set<MediaEntity>();
    public DbSet<BlobEntity> MediaBlobs => Set<BlobEntity>();
    public DbSet<TranscriptionEntity> MediaTranscriptions => Set<TranscriptionEntity>();
    public DbSet<HookOrderEntity> HookOrders => Set<HookOrderEntity>();
    public DbSet<HookEntryEntity> HookOrderEntries => Set<HookEntryEntity>();

    /// <summary>声明实际查询需要的索引、外键和并发约束。</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.AdapterId, item.BotAccountId, item.DeduplicationKey }).IsUnique();
            entity.HasIndex(item => new { item.AdapterId, item.BotAccountId, item.PlatformId, item.ConversationKind, item.ConversationId, item.ReceivedAt, item.Id }).HasDatabaseName("IX_Messages_Conversation_Received");
            entity.HasIndex(item => new { item.AdapterId, item.BotAccountId, item.PlatformId, item.ConversationKind, item.ConversationId, item.OccurredAt, item.Id }).HasDatabaseName("IX_Messages_Conversation_Occurred");
            entity.HasIndex(item => new { item.SenderId, item.ReceivedAt, item.Id }).HasDatabaseName("IX_Messages_Sender_Received");
            entity.HasIndex(item => new { item.SenderId, item.OccurredAt, item.Id }).HasDatabaseName("IX_Messages_Sender_Occurred");
            entity.HasIndex(item => new { item.RunId, item.Processing, item.Id });
            entity.HasMany(item => item.Segments).WithOne().HasForeignKey(item => item.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SegmentEntity>(entity =>
        {
            entity.ToTable("MessageSegments");
            entity.HasKey(item => new { item.MessageId, item.Position });
            entity.HasIndex(item => new { item.Kind, item.MessageId });
            entity.HasOne<MediaEntity>().WithMany().HasForeignKey(item => item.MediaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MediaEntity>(entity =>
        {
            entity.ToTable("MediaReferences");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.Claimed, item.State, item.CreatedAt, item.Id });
            entity.HasIndex(item => new { item.SourceKey, item.State });
        });
        modelBuilder.Entity<BlobEntity>(entity =>
        {
            entity.ToTable("MediaBlobs");
            entity.HasKey(item => item.ContentKey);
        });
        modelBuilder.Entity<TranscriptionEntity>(entity =>
        {
            entity.ToTable("MediaTranscriptions");
            entity.HasKey(item => new { item.ContentKey, item.Kind });
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasOne<BlobEntity>().WithMany().HasForeignKey(item => item.ContentKey).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<HookOrderEntity>(entity =>
        {
            entity.ToTable("HookOrders");
            entity.HasKey(item => new { item.HookId, item.Kind });
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.HasMany(item => item.Entries).WithOne().HasForeignKey(item => new { item.HookId, item.Kind }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<HookEntryEntity>(entity =>
        {
            entity.ToTable("HookOrderEntries");
            entity.HasKey(item => new { item.HookId, item.Kind, item.PluginId, item.HandlerId });
            entity.HasIndex(item => new { item.HookId, item.Kind, item.Position }).IsUnique();
        });
    }
}

/// <summary>消息来源与业务状态，原始内容保存在独立段表中。</summary>
public sealed class MessageEntity
{
    public long Id { get; set; }
    public string AdapterId { get; set; } = "";
    public string PlatformId { get; set; } = "";
    public string BotAccountId { get; set; } = "";
    public string ConversationKind { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string? PlatformMessageId { get; set; }
    public string? DeduplicationKey { get; set; }
    public long ReceivedAt { get; set; }
    public long? OccurredAt { get; set; }
    public MessageDirection Direction { get; set; }
    public bool Recalled { get; set; }
    public bool Deleted { get; set; }
    public ProcessingState Processing { get; set; }
    public DeliveryState Delivery { get; set; }
    public string? DeliveryError { get; set; }
    public Guid RunId { get; set; }
    public List<SegmentEntity> Segments { get; set; } = [];
}

/// <summary>有序原始消息段，媒体引用不直接等同于文件所有权。</summary>
public sealed class SegmentEntity
{
    public long MessageId { get; set; }
    public int Position { get; set; }
    public SegmentKind Kind { get; set; }
    public string? Text { get; set; }
    public Guid? MediaId { get; set; }
}

/// <summary>独立媒体引用及可恢复的文件提交状态。</summary>
public sealed class MediaEntity
{
    public Guid Id { get; set; }
    public SegmentKind Kind { get; set; }
    public string Source { get; set; } = "";
    public string? SourceKey { get; set; }
    public MediaState State { get; set; }
    public long CreatedAt { get; set; }
    public bool Claimed { get; set; }
    public string? ContentKey { get; set; }
    public long? Length { get; set; }
    public string? TemporaryPath { get; set; }
    public string? RelativePath { get; set; }
    public string? Error { get; set; }
}

/// <summary>按内容共享的原件元数据。</summary>
public sealed class BlobEntity
{
    public string ContentKey { get; set; } = "";
    public long Length { get; set; }
    public string RelativePath { get; set; } = "";
}

/// <summary>成功转述和最近失败尝试分开存放，更新使用应用修订号。</summary>
public sealed class TranscriptionEntity
{
    public string ContentKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public long? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public long Revision { get; set; }
}

/// <summary>同一钩子执行阶段的一份排序配置。</summary>
public sealed class HookOrderEntity
{
    public string HookId { get; set; } = "";
    public HandlerKind Kind { get; set; }
    public long Revision { get; set; }
    public bool Customized { get; set; }
    public List<HookEntryEntity> Entries { get; set; } = [];
}

/// <summary>稳定处理器在用户顺序中的位置，停用不会删除该记录。</summary>
public sealed class HookEntryEntity
{
    public string HookId { get; set; } = "";
    public HandlerKind Kind { get; set; }
    public string PluginId { get; set; } = "";
    public string HandlerId { get; set; } = "";
    public int Position { get; set; }
}

/// <summary>迁移工具直接创建存储上下文，不启动业务或插件。</summary>
public sealed class MissiliDesignTimeFactory : IDesignTimeDbContextFactory<MissiliDbContext>
{
    /// <summary>创建仅供设计时模型读取的上下文，不主动连接数据库。</summary>
    public MissiliDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<MissiliDbContext> options = new DbContextOptionsBuilder<MissiliDbContext>()
            .UseSqlite("Data Source=missili-design.db").Options;
        return new MissiliDbContext(options);
    }
}
