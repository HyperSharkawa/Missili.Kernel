using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>出站批次保存内容与归属，逐条平台消息仍使用原消息表。</summary>
public sealed partial class MissiliDbContext
{
    public DbSet<OutgoingBatchEntity> OutgoingBatches => Set<OutgoingBatchEntity>();
    public DbSet<OutgoingBatchMessageEntity> OutgoingBatchMessages => Set<OutgoingBatchMessageEntity>();

    /// <summary>稳定批次位置关联发送意图，任何读取都不能生成新的发送工作。</summary>
    private static void ConfigureOutgoing(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutgoingBatchEntity>(entity =>
        {
            entity.ToTable("OutgoingBatches"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.ConversationKey, item.CreatedAt, item.Id });
            entity.HasIndex(item => new { item.TaskId, item.CreatedAt, item.Id });
            entity.HasIndex(item => item.ToolInvocationId);
            entity.HasIndex(item => new { item.RunId, item.FinishedAt });
        });
        modelBuilder.Entity<OutgoingBatchMessageEntity>(entity =>
        {
            entity.ToTable("OutgoingBatchMessages"); entity.HasKey(item => new { item.BatchId, item.Position });
            entity.HasIndex(item => item.MessageId).IsUnique();
            entity.HasOne<OutgoingBatchEntity>().WithMany().HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Message).WithMany().HasForeignKey(item => item.MessageId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>原始与最终内容分开保存，审批结果不覆盖任何生成记录。</summary>
public sealed class OutgoingBatchEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ConversationKey { get; set; } = "";
    public Guid? TaskId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? ToolInvocationId { get; set; }
    public string Purpose { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? FinishedAt { get; set; }
    public BatchSendState State { get; set; }
    public string OriginalJson { get; set; } = "";
    public string FinalJson { get; set; } = "[]";
    public string ProcessorsJson { get; set; } = "[]";
    public string? ProcessingJson { get; set; }
    public string? ApprovalRequestJson { get; set; }
    public string? ApprovalJson { get; set; }
    public string? ResultJson { get; set; }
}

/// <summary>关联实际意图身份而非正文，旧本地消息标识仍可经别名定位规范记录。</summary>
public sealed class OutgoingBatchMessageEntity
{
    public Guid BatchId { get; set; }
    public int Position { get; set; }
    public long MessageId { get; set; }
    public MessageEntity Message { get; set; } = null!;
}
