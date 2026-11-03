using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Missili.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtensibleMessagesAndPlatformEvents : Migration
    {
        /// <summary>保留第一阶段数据，转移媒体关联并转换稳定类型和来源描述。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite 重建旧段表前暂存关联，不能把原 MediaId 当作 JSON 载荷丢弃。
            migrationBuilder.Sql("CREATE TEMP TABLE __PhaseTwoMediaLinks AS SELECT MessageId, Position, MediaId FROM MessageSegments WHERE MediaId IS NOT NULL;");
            migrationBuilder.DropForeignKey(
                name: "FK_MessageSegments_MediaReferences_MediaId",
                table: "MessageSegments");

            migrationBuilder.DropIndex(
                name: "IX_MessageSegments_MediaId",
                table: "MessageSegments");

            migrationBuilder.RenameColumn(
                name: "MediaId",
                table: "MessageSegments",
                newName: "PayloadJson");

            migrationBuilder.RenameColumn(
                name: "Source",
                table: "MediaReferences",
                newName: "SourceJson");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "MessageSegments",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<Guid>(
                name: "ContentId",
                table: "MessageSegments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParseError",
                table: "MessageSegments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParseState",
                table: "MessageSegments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "MessageSegments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "CanonicalId",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdapterId",
                table: "MediaReferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepresentationJson",
                table: "MediaReferences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdapterId = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformId = table.Column<string>(type: "TEXT", nullable: false),
                    BotAccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SubType = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    ConversationKind = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    RecallsMessage = table.Column<bool>(type: "INTEGER", nullable: false),
                    Processing = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StructuredContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    SourceJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdapterId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Claimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NodesJson = table.Column<string>(type: "TEXT", nullable: false),
                    IssuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CheckpointJson = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuredContents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    SegmentPosition = table.Column<int>(type: "INTEGER", nullable: true),
                    ContentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaBindings", x => x.Id);
                    table.CheckConstraint("CK_MediaBindings_Owner", "(\"MessageId\" IS NOT NULL AND \"SegmentPosition\" IS NOT NULL AND \"ContentId\" IS NULL) OR (\"MessageId\" IS NULL AND \"SegmentPosition\" IS NULL AND \"ContentId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MediaBindings_MediaReferences_MediaId",
                        column: x => x.MediaId,
                        principalTable: "MediaReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaBindings_MessageSegments_MessageId_SegmentPosition",
                        columns: x => new { x.MessageId, x.SegmentPosition },
                        principalTable: "MessageSegments",
                        principalColumns: new[] { "MessageId", "Position" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaBindings_StructuredContents_ContentId",
                        column: x => x.ContentId,
                        principalTable: "StructuredContents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO MediaBindings (Id, MessageId, SegmentPosition, ContentId, Path, Ordinal, MediaId)
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))),
                    MessageId, Position, NULL, '', 0, MediaId FROM __PhaseTwoMediaLinks;
                UPDATE MessageSegments SET Kind = CASE CAST(Kind AS TEXT)
                    WHEN '0' THEN 'text' WHEN '1' THEN 'image' WHEN '2' THEN 'audio' WHEN '3' THEN 'video' ELSE Kind END,
                    PayloadJson = NULL, Version = 1;
                UPDATE MediaReferences SET SourceJson = json_object('Kind', 'http', 'Version', 1,
                    'Data', json_object('url', SourceJson), 'StableKey', CASE WHEN SourceKey IS NULL THEN NULL ELSE json_extract(SourceKey, '$[2]') END);
                DROP TABLE __PhaseTwoMediaLinks;
                UPDATE Messages AS current SET CanonicalId = (
                    SELECT MIN(other.Id) FROM Messages AS other WHERE other.AdapterId = current.AdapterId
                    AND other.BotAccountId = current.BotAccountId AND other.PlatformId = current.PlatformId
                    AND other.ConversationKind = current.ConversationKind AND other.ConversationId = current.ConversationId
                    AND other.Direction = current.Direction AND other.PlatformMessageId = current.PlatformMessageId
                ) WHERE current.PlatformMessageId IS NOT NULL AND current.Id <> (
                    SELECT MIN(other.Id) FROM Messages AS other WHERE other.AdapterId = current.AdapterId
                    AND other.BotAccountId = current.BotAccountId AND other.PlatformId = current.PlatformId
                    AND other.ConversationKind = current.ConversationKind AND other.ConversationId = current.ConversationId
                    AND other.Direction = current.Direction AND other.PlatformMessageId = current.PlatformMessageId
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MessageSegments_ContentId",
                table: "MessageSegments",
                column: "ContentId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AdapterId_BotAccountId_PlatformId_ConversationKind_ConversationId_Direction_PlatformMessageId",
                table: "Messages",
                columns: new[] { "AdapterId", "BotAccountId", "PlatformId", "ConversationKind", "ConversationId", "Direction", "PlatformMessageId" },
                unique: true,
                filter: "\"PlatformMessageId\" IS NOT NULL AND \"CanonicalId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CanonicalId",
                table: "Messages",
                column: "CanonicalId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaBindings_ContentId_Path_Ordinal",
                table: "MediaBindings",
                columns: new[] { "ContentId", "Path", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaBindings_MediaId",
                table: "MediaBindings",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaBindings_MessageId_SegmentPosition_Ordinal",
                table: "MediaBindings",
                columns: new[] { "MessageId", "SegmentPosition", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_AdapterId_BotAccountId_ConversationKind_ConversationId_ReceivedAt_Id",
                table: "PlatformEvents",
                columns: new[] { "AdapterId", "BotAccountId", "ConversationKind", "ConversationId", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_AdapterId_BotAccountId_DeduplicationKey",
                table: "PlatformEvents",
                columns: new[] { "AdapterId", "BotAccountId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_AdapterId_BotAccountId_PlatformId_ConversationKind_ConversationId_TargetMessageId_RecallsMessage",
                table: "PlatformEvents",
                columns: new[] { "AdapterId", "BotAccountId", "PlatformId", "ConversationKind", "ConversationId", "TargetMessageId", "RecallsMessage" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_RunId_Processing_Id",
                table: "PlatformEvents",
                columns: new[] { "RunId", "Processing", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformEvents_Type_ReceivedAt_Id",
                table: "PlatformEvents",
                columns: new[] { "Type", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StructuredContents_Claimed_State_CreatedAt",
                table: "StructuredContents",
                columns: new[] { "Claimed", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StructuredContents_SourceKey",
                table: "StructuredContents",
                column: "SourceKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Messages_CanonicalId",
                table: "Messages",
                column: "CanonicalId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageSegments_StructuredContents_ContentId",
                table: "MessageSegments",
                column: "ContentId",
                principalTable: "StructuredContents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>可表达为第一阶段数据时恢复旧模型；含新增业务数据时阻止有损降级。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 先验证再变更，支持纯第一阶段数据往返，不静默删除旧模型无法表达的内容。
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __PhaseTwoRollbackGuard (Supported INTEGER NOT NULL CHECK (Supported = 1));
                INSERT INTO __PhaseTwoRollbackGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM PlatformEvents) OR EXISTS (SELECT 1 FROM StructuredContents)
                    OR EXISTS (SELECT 1 FROM MessageSegments WHERE Kind NOT IN ('text','image','audio','video')
                        OR Version <> 1 OR PayloadJson IS NOT NULL OR ParseState <> 0 OR ParseError IS NOT NULL OR ContentId IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM Messages WHERE MetadataJson IS NOT NULL OR Delivery = 7 OR CanonicalId IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM MediaReferences WHERE json_extract(SourceJson, '$.Kind') <> 'http'
                        OR Kind NOT IN (1,2,3) OR State = 6 OR RepresentationJson IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM MediaBindings GROUP BY MessageId, SegmentPosition HAVING COUNT(*) > 1)
                    THEN 0 ELSE 1 END;
                DROP TABLE __PhaseTwoRollbackGuard;
                CREATE TEMP TABLE __PhaseTwoOldLinks AS SELECT MessageId, SegmentPosition, MediaId FROM MediaBindings;
                UPDATE MessageSegments SET PayloadJson = (SELECT MediaId FROM __PhaseTwoOldLinks
                    WHERE __PhaseTwoOldLinks.MessageId = MessageSegments.MessageId AND __PhaseTwoOldLinks.SegmentPosition = MessageSegments.Position),
                    Kind = CASE Kind WHEN 'text' THEN 0 WHEN 'image' THEN 1 WHEN 'audio' THEN 2 WHEN 'video' THEN 3 END;
                UPDATE MediaReferences SET SourceJson = json_extract(SourceJson, '$.Data.url');
                DROP TABLE __PhaseTwoOldLinks;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Messages_CanonicalId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_MessageSegments_StructuredContents_ContentId",
                table: "MessageSegments");

            migrationBuilder.DropTable(
                name: "MediaBindings");

            migrationBuilder.DropTable(
                name: "PlatformEvents");

            migrationBuilder.DropTable(
                name: "StructuredContents");

            migrationBuilder.DropIndex(
                name: "IX_MessageSegments_ContentId",
                table: "MessageSegments");

            migrationBuilder.DropIndex(
                name: "IX_Messages_AdapterId_BotAccountId_PlatformId_ConversationKind_ConversationId_Direction_PlatformMessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_CanonicalId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ContentId",
                table: "MessageSegments");

            migrationBuilder.DropColumn(
                name: "ParseError",
                table: "MessageSegments");

            migrationBuilder.DropColumn(
                name: "ParseState",
                table: "MessageSegments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MessageSegments");

            migrationBuilder.DropColumn(
                name: "CanonicalId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AdapterId",
                table: "MediaReferences");

            migrationBuilder.DropColumn(
                name: "RepresentationJson",
                table: "MediaReferences");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                table: "MessageSegments",
                newName: "MediaId");

            migrationBuilder.RenameColumn(
                name: "SourceJson",
                table: "MediaReferences",
                newName: "Source");

            migrationBuilder.AlterColumn<int>(
                name: "Kind",
                table: "MessageSegments",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_MessageSegments_MediaId",
                table: "MessageSegments",
                column: "MediaId");

            migrationBuilder.AddForeignKey(
                name: "FK_MessageSegments_MediaReferences_MediaId",
                table: "MessageSegments",
                column: "MediaId",
                principalTable: "MediaReferences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
