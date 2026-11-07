using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>长词使用 trigram 候选再核对字面包含，短词在当前会话内使用参数化子串查询。</summary>
public sealed partial class SqliteStore : IHistorySearch
{
    /// <summary>查询值全部参数化，百分号、下划线及引号不成为查询运算符。</summary>
    public Task<HistorySearchPage> SearchAsync(HistorySearchQuery query, CancellationToken cancellationToken = default)
    {
        query.Conversation.Validate();
        if (string.IsNullOrWhiteSpace(query.Text) || query.PageSize is < 1 or > 100 || query.Kinds.IsDefault || query.Kinds.Any(kind => !Enum.IsDefined(kind)) ||
            !Enum.IsDefined(query.Visibility) || query.From > query.Until || query.Before is { Id: < 1 }) throw new ArgumentException("搜索条件、范围或分页参数无效。");
        string normalized = SearchValues.Fold(query.Text);
        bool indexed = normalized.EnumerateRunes().Count() >= 3;
        string phrase = "\"" + normalized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string scope = System.Text.Json.JsonSerializer.Serialize(query.Conversation);
        long? from = query.From?.ToUnixTimeMilliseconds(), until = query.Until?.ToUnixTimeMilliseconds(), beforeTime = query.Before?.At.ToUnixTimeMilliseconds();
        HistoryRecordKind[] kinds = query.Kinds.ToArray();
        return ReadAsync(database =>
        {
            string sql = indexed
                ? "SELECT d.* FROM SearchDocuments AS d JOIN SearchIndex ON SearchIndex.rowid = d.Id WHERE SearchIndex MATCH $phrase AND d.ConversationKey = $scope AND instr(d.NormalizedText, $term) > 0"
                : "SELECT d.* FROM SearchDocuments AS d WHERE d.ConversationKey = $scope AND instr(d.NormalizedText, $term) > 0";
            List<object> parameters = [new SqliteParameter("$scope", scope), new SqliteParameter("$term", normalized)];
            if (indexed) parameters.Add(new SqliteParameter("$phrase", phrase));
            IQueryable<SearchDocumentEntity> source = database.SearchDocuments.FromSqlRaw(sql, parameters.ToArray()).AsNoTracking();
            if (query.Visibility == MessageVisibilityPolicy.Placeholder) source = source.Where(document => !document.Hidden);
            if (query.SenderId is not null) source = source.Where(document => document.SenderId == query.SenderId);
            if (from.HasValue) source = source.Where(document => document.At >= from.Value);
            if (until.HasValue) source = source.Where(document => document.At < until.Value);
            if (kinds.Length > 0) source = source.Where(document => kinds.Contains(document.Kind));
            if (query.Before is HistorySearchCursor before) source = source.Where(document => document.At < beforeTime || document.At == beforeTime && document.Id < before.Id);
            List<SearchDocumentEntity> rows = source.OrderByDescending(document => document.At).ThenByDescending(document => document.Id).Take(query.PageSize + 1).ToList();
            bool more = rows.Count > query.PageSize;
            if (more) rows.RemoveAt(rows.Count - 1);
            return new HistorySearchPage
            {
                Hits = rows.Select(row => SearchHit(row, query.Visibility, normalized)).ToImmutableArray(),
                Next = more ? new HistorySearchCursor { At = DateTimeOffset.FromUnixTimeMilliseconds(rows[^1].At), Id = rows[^1].Id } : null
            };
        }, cancellationToken);
    }

    /// <summary>片段按 Unicode 文本元素截取并明确标记，不把摘要或截断伪装为完整原记录。</summary>
    private static HistorySearchHit SearchHit(SearchDocumentEntity row, MessageVisibilityPolicy visibility, string keyword)
    {
        int[] boundaries = StringInfo.ParseCombiningCharacters(row.Text);
        bool partial = boundaries.Length > 240;
        string text = row.Text;
        if (partial)
        {
            int found = Array.BinarySearch(boundaries, Math.Clamp(row.NormalizedText.IndexOf(keyword, StringComparison.Ordinal), 0, row.Text.Length));
            int element = found >= 0 ? found : Math.Max(0, ~found - 1);
            int first = Math.Max(0, element - 60), last = Math.Min(boundaries.Length, first + 240);
            text = (first > 0 ? "…" : "") + row.Text[boundaries[first]..(last < boundaries.Length ? boundaries[last] : row.Text.Length)] + (last < boundaries.Length ? "…" : "");
        }
        bool hidden = row.Hidden && visibility == MessageVisibilityPolicy.MarkedOriginal;
        return new HistorySearchHit
        {
            Id = row.Id,
            Kind = row.Kind,
            SourceId = row.SourceId,
            At = DateTimeOffset.FromUnixTimeMilliseconds(row.At),
            SenderId = row.SenderId,
            TaskId = row.TaskId,
            AgentId = row.AgentId,
            MessageId = row.MessageId,
            Text = hidden ? "[已撤回或删除] " + text : text,
            Partial = partial,
            HiddenOriginal = hidden
        };
    }
}
