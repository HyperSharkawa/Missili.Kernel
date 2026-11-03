using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Missili.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HookOrders",
                columns: table => new
                {
                    HookId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Customized = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HookOrders", x => new { x.HookId, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "MediaBlobs",
                columns: table => new
                {
                    ContentKey = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaBlobs", x => x.ContentKey);
                });

            migrationBuilder.CreateTable(
                name: "MediaReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Claimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", nullable: true),
                    Length = table.Column<long>(type: "INTEGER", nullable: true),
                    TemporaryPath = table.Column<string>(type: "TEXT", nullable: true),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaReferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdapterId = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformId = table.Column<string>(type: "TEXT", nullable: false),
                    BotAccountId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationKind = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Recalled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Processing = table.Column<int>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryError = table.Column<string>(type: "TEXT", nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HookOrderEntries",
                columns: table => new
                {
                    HookId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    HandlerId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HookOrderEntries", x => new { x.HookId, x.Kind, x.PluginId, x.HandlerId });
                    table.ForeignKey(
                        name: "FK_HookOrderEntries_HookOrders_HookId_Kind",
                        columns: x => new { x.HookId, x.Kind },
                        principalTable: "HookOrders",
                        principalColumns: new[] { "HookId", "Kind" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaTranscriptions",
                columns: table => new
                {
                    ContentKey = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaTranscriptions", x => new { x.ContentKey, x.Kind });
                    table.ForeignKey(
                        name: "FK_MediaTranscriptions_MediaBlobs_ContentKey",
                        column: x => x.ContentKey,
                        principalTable: "MediaBlobs",
                        principalColumn: "ContentKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageSegments",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    MediaId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageSegments", x => new { x.MessageId, x.Position });
                    table.ForeignKey(
                        name: "FK_MessageSegments_MediaReferences_MediaId",
                        column: x => x.MediaId,
                        principalTable: "MediaReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageSegments_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HookOrderEntries_HookId_Kind_Position",
                table: "HookOrderEntries",
                columns: new[] { "HookId", "Kind", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaReferences_Claimed_State_CreatedAt_Id",
                table: "MediaReferences",
                columns: new[] { "Claimed", "State", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaReferences_SourceKey_State",
                table: "MediaReferences",
                columns: new[] { "SourceKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AdapterId_BotAccountId_DeduplicationKey",
                table: "Messages",
                columns: new[] { "AdapterId", "BotAccountId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Conversation_Occurred",
                table: "Messages",
                columns: new[] { "AdapterId", "BotAccountId", "PlatformId", "ConversationKind", "ConversationId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Conversation_Received",
                table: "Messages",
                columns: new[] { "AdapterId", "BotAccountId", "PlatformId", "ConversationKind", "ConversationId", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_RunId_Processing_Id",
                table: "Messages",
                columns: new[] { "RunId", "Processing", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Sender_Occurred",
                table: "Messages",
                columns: new[] { "SenderId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Sender_Received",
                table: "Messages",
                columns: new[] { "SenderId", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageSegments_Kind_MessageId",
                table: "MessageSegments",
                columns: new[] { "Kind", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageSegments_MediaId",
                table: "MessageSegments",
                column: "MediaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HookOrderEntries");

            migrationBuilder.DropTable(
                name: "MediaTranscriptions");

            migrationBuilder.DropTable(
                name: "MessageSegments");

            migrationBuilder.DropTable(
                name: "HookOrders");

            migrationBuilder.DropTable(
                name: "MediaBlobs");

            migrationBuilder.DropTable(
                name: "MediaReferences");

            migrationBuilder.DropTable(
                name: "Messages");
        }
    }
}
