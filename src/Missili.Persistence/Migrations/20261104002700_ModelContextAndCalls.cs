using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Missili.Persistence.Migrations
{
    /// <summary>增加模型调用和上下文窗口，并保留旧转述来源数据。</summary>
    public partial class ModelContextAndCalls : Migration
    {
        /// <summary>先迁移来源字段，再删除已被结构化来源替代的旧列。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcesJson",
                table: "MediaTranscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql("""
                UPDATE MediaTranscriptions SET SourcesJson = json_array(json_object(
                    'Provider', Provider, 'Model', Model, 'Prompt', Prompt, 'Batch', 0, 'CallId', NULL))
                WHERE Provider IS NOT NULL OR Model IS NOT NULL OR Prompt IS NOT NULL;
                """);
            migrationBuilder.DropColumn(name: "Model", table: "MediaTranscriptions");
            migrationBuilder.DropColumn(name: "Prompt", table: "MediaTranscriptions");
            migrationBuilder.DropColumn(name: "Provider", table: "MediaTranscriptions");

            migrationBuilder.CreateTable(
                name: "ConversationWindows",
                columns: table => new
                {
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: false),
                    DiscardedThrough = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationWindows", x => x.ConversationKey);
                });

            migrationBuilder.CreateTable(
                name: "MediaRepresentations",
                columns: table => new
                {
                    ContentKey = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    MediaId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRepresentations", x => new { x.ContentKey, x.Kind });
                    table.ForeignKey(
                        name: "FK_MediaRepresentations_MediaBlobs_ContentKey",
                        column: x => x.ContentKey,
                        principalTable: "MediaBlobs",
                        principalColumn: "ContentKey",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaRepresentations_MediaReferences_MediaId",
                        column: x => x.MediaId,
                        principalTable: "MediaReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModelCalls",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", nullable: true),
                    TriggerMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCalls", x => x.Sequence);
                    table.UniqueConstraint("AK_ModelCalls_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<Guid>(type: "TEXT", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorKind = table.Column<string>(type: "TEXT", nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CachedInputTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    HasPartialResult = table.Column<bool>(type: "INTEGER", nullable: false),
                    DetailId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelAttempts_ModelCalls_CallId",
                        column: x => x.CallId,
                        principalTable: "ModelCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRepresentations_MediaId",
                table: "MediaRepresentations",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelAttempts_CallId_Sequence",
                table: "ModelAttempts",
                columns: new[] { "CallId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_ConversationKey_Sequence",
                table: "ModelCalls",
                columns: new[] { "ConversationKey", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_Purpose_Sequence",
                table: "ModelCalls",
                columns: new[] { "Purpose", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_RunId_State",
                table: "ModelCalls",
                columns: new[] { "RunId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_StartedAt_Sequence",
                table: "ModelCalls",
                columns: new[] { "StartedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCalls_State_Sequence",
                table: "ModelCalls",
                columns: new[] { "State", "Sequence" });
        }

        /// <summary>仅允许没有新增业务语义的数据无损降级；拒绝丢弃调用、窗口或多批来源。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __PhaseThreeRollbackGuard (Supported INTEGER NOT NULL CHECK (Supported = 1));
                INSERT INTO __PhaseThreeRollbackGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM ModelCalls) OR EXISTS (SELECT 1 FROM ConversationWindows) OR
                    EXISTS (SELECT 1 FROM MediaRepresentations) OR EXISTS (
                        SELECT 1 FROM MediaTranscriptions WHERE json_array_length(SourcesJson) > 1
                        OR json_extract(SourcesJson, '$[0].CallId') IS NOT NULL
                        OR COALESCE(json_extract(SourcesJson, '$[0].Batch'), 0) != 0)
                    THEN 0 ELSE 1 END;
                DROP TABLE __PhaseThreeRollbackGuard;
                """);
            migrationBuilder.AddColumn<string>(name: "Model", table: "MediaTranscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Provider", table: "MediaTranscriptions", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Prompt", table: "MediaTranscriptions", type: "TEXT", nullable: true);
            migrationBuilder.Sql("""
                UPDATE MediaTranscriptions SET Model = json_extract(SourcesJson, '$[0].Model'),
                    Provider = json_extract(SourcesJson, '$[0].Provider'), Prompt = json_extract(SourcesJson, '$[0].Prompt');
                """);
            migrationBuilder.DropColumn(name: "SourcesJson", table: "MediaTranscriptions");
            migrationBuilder.DropTable(name: "ModelAttempts");
            migrationBuilder.DropTable(name: "ModelCalls");
            migrationBuilder.DropTable(name: "ConversationWindows");
            migrationBuilder.DropTable(name: "MediaRepresentations");
        }
    }
}
