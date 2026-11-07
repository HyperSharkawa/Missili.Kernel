using Microsoft.EntityFrameworkCore.Migrations;

namespace Missili.Persistence.Migrations;

/// <summary>第四阶段检索 DDL 固定在本迁移中，源投影和 FTS 更新与源记录处于同一事务。</summary>
public partial class AgentRuntimeAndOutgoing
{
    /// <summary>回填既有聊天并登记源更新触发器，原始记录和媒体不被改写。</summary>
    private static void CreateSearchIndex(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE VIRTUAL TABLE SearchIndex USING fts5(NormalizedText, content='SearchDocuments', content_rowid='Id', tokenize='trigram case_sensitive 1');
            CREATE TRIGGER SearchDocuments_Insert AFTER INSERT ON SearchDocuments BEGIN
                INSERT INTO SearchIndex(rowid, NormalizedText) VALUES (NEW.Id, NEW.NormalizedText);
            END;
            CREATE TRIGGER SearchDocuments_Update AFTER UPDATE ON SearchDocuments BEGIN
                INSERT INTO SearchIndex(SearchIndex, rowid, NormalizedText) VALUES ('delete', OLD.Id, OLD.NormalizedText);
                INSERT INTO SearchIndex(rowid, NormalizedText) VALUES (NEW.Id, NEW.NormalizedText);
            END;
            CREATE TRIGGER SearchDocuments_Delete AFTER DELETE ON SearchDocuments BEGIN
                INSERT INTO SearchIndex(SearchIndex, rowid, NormalizedText) VALUES ('delete', OLD.Id, OLD.NormalizedText);
            END;

            CREATE VIEW SearchChatSource AS
            SELECT 0 AS Kind, CAST(Id AS TEXT) AS SourceId,
                missili_address(json_object('AdapterId', AdapterId, 'PlatformId', PlatformId, 'BotAccountId', BotAccountId, 'ConversationKind', ConversationKind, 'ConversationId', ConversationId)) AS ConversationKey,
                ReceivedAt AS At, SenderId, NULL AS TaskId, NULL AS AgentId, Id AS MessageId, SearchText AS Text, missili_fold(SearchText) AS NormalizedText, (Recalled OR Deleted) AS Hidden
            FROM (
                SELECT m.*, COALESCE((SELECT group_concat(Text, '') FROM
                    (SELECT s.Text FROM MessageSegments s WHERE s.MessageId = m.Id AND s.Kind = 'text' AND s.ParseState = 0 ORDER BY s.Position)), '') AS SearchText
                FROM Messages m WHERE m.CanonicalId IS NULL AND (m.Direction = 0 OR m.Delivery = 3)
            ) WHERE SearchText <> '';

            CREATE VIEW SearchEntrySource AS
            SELECT CASE WHEN Kind = 5 THEN 2 ELSE 1 END AS Kind, CAST(Id AS TEXT) AS SourceId, ConversationKey,
                CreatedAt AS At, SenderId, TaskId, AgentId, SourceMessageId AS MessageId, SearchText AS Text, missili_fold(SearchText) AS NormalizedText, Hidden
            FROM (
                SELECT e.*, t.ConversationKey, m.SenderId, COALESCE(m.Recalled OR m.Deleted, 0) AS Hidden,
                    CASE WHEN e.Kind = 0 THEN COALESCE((SELECT group_concat(json_extract(value, '$.Text'), '')
                        FROM json_each(e.DataJson, '$.Content') WHERE json_extract(value, '$.Kind') = 'text' AND json_extract(value, '$.ParseState') = 0), '')
                    WHEN e.Kind = 4 THEN COALESCE(json_extract(e.DataJson, '$.task'), json_extract(e.DataJson, '$.text'), '')
                    ELSE COALESCE((SELECT group_concat(json_extract(value, '$.Text'), '') FROM json_each(e.DataJson, '$.Content') WHERE json_extract(value, '$.Kind') = 0), '')
                        || COALESCE(char(10) || json_extract(e.DataJson, '$.Unfinished'), '') || COALESCE(char(10) || json_extract(e.DataJson, '$.Error'), '') END AS SearchText
                FROM AgentEntries e JOIN AgentTasks t ON t.Id = e.TaskId LEFT JOIN Messages m ON m.Id = e.SourceMessageId
                WHERE e.Kind IN (0, 4, 5)
            ) WHERE SearchText <> '';

            CREATE VIEW SearchToolSource AS
            SELECT 3 AS Kind, CAST(Id AS TEXT) AS SourceId, ConversationKey, CreatedAt AS At, NULL AS SenderId,
                TaskId, AgentId, NULL AS MessageId, SearchText AS Text, missili_fold(SearchText) AS NormalizedText, 0 AS Hidden
            FROM (
                SELECT c.*, t.ConversationKey,
                    COALESCE(json_extract(c.ExecutionJson, '$.Text'), json_extract(c.ResultJson, '$.Execution.Text'), '')
                        || COALESCE(char(10) || json_extract(c.ResultJson, '$.Error'), '') AS SearchText
                FROM AgentToolCalls c JOIN AgentTasks t ON t.Id = c.TaskId
            ) WHERE SearchText <> '';
            """);
        string chatInsert = UpsertSearch("SearchChatSource", "MessageId = NEW.Id");
        string chatDelete = "DELETE FROM SearchDocuments WHERE Kind = 0 AND MessageId = NEW.Id AND NOT EXISTS (SELECT 1 FROM SearchChatSource WHERE MessageId = NEW.Id);";
        string entries = UpsertSearch("SearchEntrySource", "MessageId = NEW.Id");
        migrationBuilder.Sql($"""
            CREATE TRIGGER SearchMessages_Insert AFTER INSERT ON Messages BEGIN {chatInsert} END;
            CREATE TRIGGER SearchMessages_Update AFTER UPDATE ON Messages BEGIN {chatDelete} {chatInsert} {entries} END;
            CREATE TRIGGER SearchMessages_Delete AFTER DELETE ON Messages BEGIN
                DELETE FROM SearchDocuments WHERE Kind = 0 AND MessageId = OLD.Id;
                UPDATE SearchDocuments SET Hidden = 1 WHERE MessageId = OLD.Id;
            END;
            """);
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string row = operation == "DELETE" ? "OLD" : "NEW";
            string changed = row + ".MessageId";
            migrationBuilder.Sql($"""
                CREATE TRIGGER SearchSegments_{operation} AFTER {operation} ON MessageSegments BEGIN
                    DELETE FROM SearchDocuments WHERE Kind = 0 AND MessageId = {changed} AND NOT EXISTS (SELECT 1 FROM SearchChatSource WHERE MessageId = {changed});
                    {UpsertSearch("SearchChatSource", "MessageId = " + changed)}
                END;
                """);
        }
        foreach ((string table, string view, string kinds) in new[] { ("AgentEntries", "SearchEntrySource", "1, 2"), ("AgentToolCalls", "SearchToolSource", "3") })
        {
            string source = UpsertSearch(view, "SourceId = CAST(NEW.Id AS TEXT)");
            migrationBuilder.Sql($"""
                CREATE TRIGGER Search{table}_Insert AFTER INSERT ON {table} BEGIN {source} END;
                CREATE TRIGGER Search{table}_Update AFTER UPDATE ON {table} BEGIN
                    DELETE FROM SearchDocuments WHERE Kind IN ({kinds}) AND SourceId = CAST(NEW.Id AS TEXT)
                        AND NOT EXISTS (SELECT 1 FROM {view} WHERE SourceId = CAST(NEW.Id AS TEXT));
                    {source}
                END;
                CREATE TRIGGER Search{table}_Delete AFTER DELETE ON {table} BEGIN
                    DELETE FROM SearchDocuments WHERE Kind IN ({kinds}) AND SourceId = CAST(OLD.Id AS TEXT);
                END;
                """);
        }
        foreach (string view in new[] { "SearchChatSource", "SearchEntrySource", "SearchToolSource" }) migrationBuilder.Sql(UpsertSearch(view, "1 = 1"));
    }

    /// <summary>只接受本迁移固定的对象名与触发器行表达式，不插入用户查询内容。</summary>
    private static string UpsertSearch(string view, string condition) => $"""
        INSERT INTO SearchDocuments(Kind, SourceId, ConversationKey, At, SenderId, TaskId, AgentId, MessageId, Text, NormalizedText, Hidden)
            SELECT Kind, SourceId, ConversationKey, At, SenderId, TaskId, AgentId, MessageId, Text, NormalizedText, Hidden FROM {view} WHERE {condition}
        ON CONFLICT(Kind, SourceId) DO UPDATE SET ConversationKey = excluded.ConversationKey, At = excluded.At, SenderId = excluded.SenderId,
            TaskId = excluded.TaskId, AgentId = excluded.AgentId, MessageId = excluded.MessageId, Text = excluded.Text, NormalizedText = excluded.NormalizedText, Hidden = excluded.Hidden;
        """;

    /// <summary>降级只删除派生搜索结构，能否丢弃 Agent 数据仍由主迁移保护检查裁决。</summary>
    private static void DropSearchIndex(MigrationBuilder migrationBuilder)
    {
        foreach (string trigger in new[] { "SearchDocuments_Insert", "SearchDocuments_Update", "SearchDocuments_Delete", "SearchMessages_Insert", "SearchMessages_Update", "SearchMessages_Delete",
            "SearchSegments_INSERT", "SearchSegments_UPDATE", "SearchSegments_DELETE", "SearchAgentEntries_Insert", "SearchAgentEntries_Update", "SearchAgentEntries_Delete",
            "SearchAgentToolCalls_Insert", "SearchAgentToolCalls_Update", "SearchAgentToolCalls_Delete" }) migrationBuilder.Sql("DROP TRIGGER " + trigger + ";");
        migrationBuilder.Sql("DROP VIEW SearchChatSource; DROP VIEW SearchEntrySource; DROP VIEW SearchToolSource; DROP TABLE SearchIndex;");
    }
}
