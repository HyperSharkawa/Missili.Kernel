using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Missili.Persistence.Migrations
{
    /// <summary>保留运行及输入原件，迁移为中性的唤醒来源和子代理分工。</summary>
    public partial class AgentWakingAndRuntimeSources : Migration
    {
        /// <summary>通过重命名保存历史描述，只为旧输入补充未知触发方式。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Task",
                table: "AgentSessions",
                newName: "Assignment");

            migrationBuilder.RenameColumn(
                name: "InitiatorId",
                table: "AgentTasks",
                newName: "TriggerSenderId");

            migrationBuilder.AlterColumn<string>(
                name: "Assignment",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.Sql("""
                UPDATE AgentEntries
                SET DataJson = json_set(DataJson, '$.Cause', json('{"Kind":0,"ConfigurationRevision":0,"MatchedText":null,"Probability":null,"Sample":null}'))
                WHERE Kind = 0 AND json_type(DataJson, '$.Cause') IS NULL;
                """);
        }

        /// <summary>回退列名时保留历史正文，新主代理的空分工转换为旧列允许的空文本。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE AgentSessions SET Assignment = '' WHERE Assignment IS NULL;");
            migrationBuilder.AlterColumn<string>(
                name: "Assignment",
                table: "AgentSessions",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.RenameColumn(name: "Assignment", table: "AgentSessions", newName: "Task");

            migrationBuilder.RenameColumn(
                name: "TriggerSenderId",
                table: "AgentTasks",
                newName: "InitiatorId");


        }
    }
}
