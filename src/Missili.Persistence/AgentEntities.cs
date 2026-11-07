using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>代理审计模型仅保存共享值，任何记录都不能直接恢复或重放业务。</summary>
public sealed partial class MissiliDbContext
{
    public DbSet<AgentTaskEntity> AgentTasks => Set<AgentTaskEntity>();
    public DbSet<AgentSessionEntity> AgentSessions => Set<AgentSessionEntity>();
    public DbSet<AgentEntryEntity> AgentEntries => Set<AgentEntryEntity>();

    /// <summary>会话归属使用独立释放标记，终态但资源未退出时仍拒绝第二个主代理。</summary>
    private static void ConfigureAgents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentTaskEntity>(entity =>
        {
            entity.ToTable("AgentTasks");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.ConversationKey).IsUnique().HasFilter("\"ReleasedAt\" IS NULL");
            entity.HasIndex(item => new { item.ConversationKey, item.CreatedAt, item.Id });
            entity.HasIndex(item => new { item.RunId, item.State });
            entity.HasMany(item => item.Agents).WithOne().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AgentSessionEntity>(entity =>
        {
            entity.ToTable("AgentSessions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TaskId, item.ParentId, item.Id });
            entity.HasOne<AgentSessionEntity>().WithMany().HasForeignKey(item => item.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AgentEntryEntity>(entity =>
        {
            entity.ToTable("AgentEntries");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.AgentId, item.Sequence }).IsUnique();
            entity.HasIndex(item => new { item.TaskId, item.CreatedAt, item.Id });
            entity.HasIndex(item => item.SourceMessageId).IsUnique().HasFilter("\"SourceMessageId\" IS NOT NULL");
            entity.HasIndex(item => new { item.AgentId, item.SourceResultAgentId, item.SourceResultRevision }).IsUnique().HasFilter("\"SourceResultAgentId\" IS NOT NULL");
            entity.HasOne<AgentSessionEntity>().WithMany().HasForeignKey(item => item.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentTaskEntity>().WithMany().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MessageEntity>().WithMany().HasForeignKey(item => item.SourceMessageId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>根任务保留初始授权和预算，会话归属直到真实工作退出才释放。</summary>
public sealed class AgentTaskEntity
{
    public Guid Id { get; set; }
    public Guid RootAgentId { get; set; }
    public Guid RunId { get; set; }
    public string ConversationKey { get; set; } = "";
    public string TriggerSenderId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public long ConfigurationRevision { get; set; }
    public string InitialProfileJson { get; set; } = "";
    public AgentTaskState State { get; set; }
    public long CreatedAt { get; set; }
    public long? FinishedAt { get; set; }
    public long? ReleasedAt { get; set; }
    public string? Error { get; set; }
    public int ModelCalls { get; set; }
    public int ToolCalls { get; set; }
    public int TotalAgents { get; set; }
    public List<AgentSessionEntity> Agents { get; set; } = [];
}

/// <summary>代理的实际活动和成果修订，空闲不是任务关闭。</summary>
public sealed class AgentSessionEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? ParentId { get; set; }
    public int Depth { get; set; }
    public string? TemplateId { get; set; }
    public string? Assignment { get; set; }
    public AgentActivity Activity { get; set; }
    public long CreatedAt { get; set; }
    public long? StopRequestedAt { get; set; }
    public long? StopDeadline { get; set; }
    public long? ExitedAt { get; set; }
    public bool MaySettle { get; set; }
    public bool SettlementUsed { get; set; }
    public int DecisionCalls { get; set; }
    public long NextSequence { get; set; }
    public long ResultRevision { get; set; }
    public string? ResultJson { get; set; }
}

/// <summary>追加式记录保留来源与顺序，源消息去重不使用正文相似度。</summary>
public sealed class AgentEntryEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AgentId { get; set; }
    public long Sequence { get; set; }
    public AgentEntryKind Kind { get; set; }
    public long CreatedAt { get; set; }
    public long? SourceMessageId { get; set; }
    public Guid? SourceResultAgentId { get; set; }
    public long? SourceResultRevision { get; set; }
    public string DataJson { get; set; } = "";
}
