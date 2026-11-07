using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Missili.Persistence.Migrations
{
    /// <summary>增加代理审计与调用归属，已有模型记录使用空候选排除列表。</summary>
    public partial class AgentRuntimeAndOutgoing : Migration
    {
        /// <summary>扩展现有数据库，不重建聊天、媒体或模型调用记录。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageContextPositions",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageContextPositions", x => x.MessageId);
                    table.ForeignKey("FK_MessageContextPositions_Messages_MessageId", x => x.MessageId, "Messages", "Id", onDelete: ReferentialAction.Restrict);
                });
            // 旧库可能已有多级别名；回填最早来源，不改变聊天原件、规范身份或既有淘汰边界。
            migrationBuilder.Sql("""
                WITH RECURSIVE Aliases(SourceId, TargetId) AS (
                    SELECT Id, CanonicalId FROM Messages WHERE CanonicalId IS NOT NULL
                    UNION ALL
                    SELECT a.SourceId, m.CanonicalId FROM Aliases a JOIN Messages m ON m.Id = a.TargetId WHERE m.CanonicalId IS NOT NULL
                )
                INSERT INTO MessageContextPositions(MessageId, Position)
                    SELECT a.TargetId, min(CASE WHEN a.SourceId < a.TargetId THEN a.SourceId ELSE a.TargetId END)
                    FROM Aliases a JOIN Messages m ON m.Id = a.TargetId WHERE m.CanonicalId IS NULL GROUP BY a.TargetId;
                """);
            migrationBuilder.CreateTable(
                name: "SearchDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedText = table.Column<string>(type: "TEXT", nullable: false),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchDocuments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchDocuments_ConversationKey_At_Id",
                table: "SearchDocuments",
                columns: new[] { "ConversationKey", "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchDocuments_Kind_SourceId",
                table: "SearchDocuments",
                columns: new[] { "Kind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchDocuments_MessageId",
                table: "SearchDocuments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchDocuments_TaskId_At_Id",
                table: "SearchDocuments",
                columns: new[] { "TaskId", "At", "Id" });
            migrationBuilder.CreateTable(
                name: "AgentToolCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelCallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: true),
                    IntentJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreparedJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovalRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovalJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentToolCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentToolCalls_AgentSessions_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentToolCalls_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentToolCalls_ModelCalls_ModelCallId",
                        column: x => x.ModelCallId,
                        principalTable: "ModelCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentToolCalls_AgentId_ModelCallId_Position",
                table: "AgentToolCalls",
                columns: new[] { "AgentId", "ModelCallId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentToolCalls_AgentId_ModelCallId_ToolCallId",
                table: "AgentToolCalls",
                columns: new[] { "AgentId", "ModelCallId", "ToolCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentToolCalls_ModelCallId",
                table: "AgentToolCalls",
                column: "ModelCallId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentToolCalls_State_CreatedAt",
                table: "AgentToolCalls",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentToolCalls_TaskId_CreatedAt_Id",
                table: "AgentToolCalls",
                columns: new[] { "TaskId", "CreatedAt", "Id" });
            migrationBuilder.CreateTable(
                name: "OutgoingBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolInvocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalJson = table.Column<string>(type: "TEXT", nullable: false),
                    FinalJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessingJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovalRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovalJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutgoingBatchMessages",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingBatchMessages", x => new { x.BatchId, x.Position });
                    table.ForeignKey(
                        name: "FK_OutgoingBatchMessages_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutgoingBatchMessages_OutgoingBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "OutgoingBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingBatches_ConversationKey_CreatedAt_Id",
                table: "OutgoingBatches",
                columns: new[] { "ConversationKey", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingBatches_RunId_FinishedAt",
                table: "OutgoingBatches",
                columns: new[] { "RunId", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingBatches_TaskId_CreatedAt_Id",
                table: "OutgoingBatches",
                columns: new[] { "TaskId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingBatches_ToolInvocationId",
                table: "OutgoingBatches",
                column: "ToolInvocationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingBatchMessages_MessageId",
                table: "OutgoingBatchMessages",
                column: "MessageId",
                unique: true);
            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExcludedCandidatesJson",
                table: "ModelCalls",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "OriginPluginId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OutgoingBatchId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToolInvocationId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TurnId",
                table: "ModelCalls",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RootAgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: false),
                    InitiatorId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    InitialProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReleasedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalAgents = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<string>(type: "TEXT", nullable: true),
                    Task = table.Column<string>(type: "TEXT", nullable: false),
                    Activity = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StopRequestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StopDeadline = table.Column<long>(type: "INTEGER", nullable: true),
                    ExitedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MaySettle = table.Column<bool>(type: "INTEGER", nullable: false),
                    SettlementUsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    DecisionCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    NextSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSessions_AgentSessions_ParentId",
                        column: x => x.ParentId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentSessions_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentEntries_AgentSessions_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEntries_AgentTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AgentTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEntries_Messages_SourceMessageId",
                        column: x => x.SourceMessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_AgentId_Sequence",
                table: "ModelCalls",
                columns: new[] { "AgentId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_TaskId_Sequence",
                table: "ModelCalls",
                columns: new[] { "TaskId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_ToolInvocationId",
                table: "ModelCalls",
                column: "ToolInvocationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntries_AgentId_Sequence",
                table: "AgentEntries",
                columns: new[] { "AgentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntries_SourceMessageId",
                table: "AgentEntries",
                column: "SourceMessageId",
                unique: true,
                filter: "\"SourceMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntries_TaskId_CreatedAt_Id",
                table: "AgentEntries",
                columns: new[] { "TaskId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_ParentId",
                table: "AgentSessions",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_TaskId_ParentId_Id",
                table: "AgentSessions",
                columns: new[] { "TaskId", "ParentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_ConversationKey",
                table: "AgentTasks",
                column: "ConversationKey",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_ConversationKey_CreatedAt_Id",
                table: "AgentTasks",
                columns: new[] { "ConversationKey", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_RunId_State",
                table: "AgentTasks",
                columns: new[] { "RunId", "State" });
            migrationBuilder.AddColumn<Guid>(
                name: "SourceResultAgentId",
                table: "AgentEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceResultRevision",
                table: "AgentEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEntries_AgentId_SourceResultAgentId_SourceResultRevision",
                table: "AgentEntries",
                columns: new[] { "AgentId", "SourceResultAgentId", "SourceResultRevision" },
                unique: true,
                filter: "\"SourceResultAgentId\" IS NOT NULL");
            CreateSearchIndex(migrationBuilder);
        }

        /// <summary>只允许无新增业务数据时降级，不能丢弃任务成果或调用归属。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __PhaseFourRollbackGuard (Supported INTEGER NOT NULL CHECK (Supported = 1));
                INSERT INTO __PhaseFourRollbackGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM AgentToolCalls) OR EXISTS (SELECT 1 FROM OutgoingBatches) OR EXISTS (SELECT 1 FROM OutgoingBatchMessages)
                    OR EXISTS (SELECT 1 FROM AgentTasks) OR EXISTS (SELECT 1 FROM AgentSessions) OR EXISTS (SELECT 1 FROM AgentEntries)
                    OR EXISTS (SELECT 1 FROM ModelCalls WHERE TaskId IS NOT NULL OR AgentId IS NOT NULL OR TurnId IS NOT NULL
                        OR ToolInvocationId IS NOT NULL OR OutgoingBatchId IS NOT NULL OR OriginPluginId IS NOT NULL
                        OR json_array_length(ExcludedCandidatesJson) > 0)
                    THEN 0 ELSE 1 END;
                DROP TABLE __PhaseFourRollbackGuard;
                """);
            DropSearchIndex(migrationBuilder);
            migrationBuilder.DropTable(name: "MessageContextPositions");
            migrationBuilder.DropTable(
                name: "SearchDocuments");
            migrationBuilder.DropTable(
                name: "AgentToolCalls");
            migrationBuilder.DropTable(
                name: "OutgoingBatchMessages");

            migrationBuilder.DropTable(
                name: "OutgoingBatches");
            migrationBuilder.DropTable(
                name: "AgentEntries");

            migrationBuilder.DropTable(
                name: "AgentSessions");

            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropIndex(
                name: "IX_ModelCalls_AgentId_Sequence",
                table: "ModelCalls");

            migrationBuilder.DropIndex(
                name: "IX_ModelCalls_TaskId_Sequence",
                table: "ModelCalls");

            migrationBuilder.DropIndex(
                name: "IX_ModelCalls_ToolInvocationId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "ExcludedCandidatesJson",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "OriginPluginId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "OutgoingBatchId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "TaskId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "ToolInvocationId",
                table: "ModelCalls");

            migrationBuilder.DropColumn(
                name: "TurnId",
                table: "ModelCalls");
        }
    }
}
