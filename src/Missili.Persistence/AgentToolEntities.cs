using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>实际工具调用使用独立身份与唯一键，模型重试不会重建已执行动作。</summary>
public sealed partial class MissiliDbContext
{
    public DbSet<AgentToolCallEntity> AgentToolCalls => Set<AgentToolCallEntity>();

    /// <summary>调用关联任务、代理与完整模型记录，禁止删除仍被业务审计引用的来源。</summary>
    private static void ConfigureAgentTools(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentToolCallEntity>(entity =>
        {
            entity.ToTable("AgentToolCalls"); entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.AgentId, item.ModelCallId, item.ToolCallId }).IsUnique();
            entity.HasIndex(item => new { item.AgentId, item.ModelCallId, item.Position }).IsUnique();
            entity.HasIndex(item => new { item.TaskId, item.CreatedAt, item.Id });
            entity.HasIndex(item => new { item.State, item.CreatedAt });
            entity.HasOne<AgentTaskEntity>().WithMany().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentSessionEntity>().WithMany().HasForeignKey(item => item.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ModelCallEntity>().WithMany().HasForeignKey(item => item.ModelCallId).HasPrincipalKey(item => item.Id).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>所有阶段只持久化共享 JSON，不保存执行委托、异常对象或私有 DTO。</summary>
public sealed class AgentToolCallEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AgentId { get; set; }
    public Guid TurnId { get; set; }
    public Guid ModelCallId { get; set; }
    public string ToolCallId { get; set; } = "";
    public int Position { get; set; }
    public string? PluginId { get; set; }
    public string IntentJson { get; set; } = "";
    public string? PreparedJson { get; set; }
    public string? ApprovalRequestJson { get; set; }
    public string? ApprovalJson { get; set; }
    public string? ExecutionJson { get; set; }
    public string? ResultJson { get; set; }
    public ToolCallState State { get; set; }
    public long CreatedAt { get; set; }
    public long? StartedAt { get; set; }
    public long? FinishedAt { get; set; }
}
