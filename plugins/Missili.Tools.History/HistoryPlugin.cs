using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Missili.Abstractions;

namespace Missili.Tools.History;

/// <summary>官方历史工具只组合宿主限定的读取能力，不访问 EF 或执行自定义查询语言。</summary>
public sealed class HistoryPlugin : IPlugin, IPluginConfigurationValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) } };
    /// <summary>登记三种实际读取工具，参数模式与执行校验采用相同的有限字段。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.ConfigurationValidators.Register(this);
        Register(context, "history.search", "search_history", "在当前获准会话按字面关键词搜索聊天、任务要求、成果及工具正文，返回有限原文片段与读取身份。",
            """{"type":"object","properties":{"query":{"type":"string"},"from":{"type":"string","format":"date-time"},"until":{"type":"string","format":"date-time"},"sender_id":{"type":"string"},"kinds":{"type":"array","items":{"type":"string","enum":["chat","agent_input","agent_result","tool_text"]}},"page_size":{"type":"integer","minimum":1,"maximum":100},"cursor":{"type":"object","properties":{"at":{"type":"string","format":"date-time"},"id":{"type":"integer","minimum":1}},"required":["at","id"],"additionalProperties":false}},"required":["query"],"additionalProperties":false}""",
            "items 包含原文片段及来源；next 原样作为 cursor 继续搜索。搜索不生成摘要。");
        Register(context, "history.read", "read_history", "分页读取当前会话的原聊天记录，指定 message_id 时可按 offset/length 展开较大记录。撤回可见性由宿主决定。",
            """{"type":"object","properties":{"message_id":{"type":"integer","minimum":1},"from":{"type":"string","format":"date-time"},"until":{"type":"string","format":"date-time"},"sender_id":{"type":"string"},"page_size":{"type":"integer","minimum":1,"maximum":100},"cursor":{"type":"object","properties":{"unix_milliseconds":{"type":"integer"},"message_id":{"type":"integer","minimum":1}},"required":["unix_milliseconds","message_id"],"additionalProperties":false},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1,"maximum":65536}},"additionalProperties":false}""",
            "列表中的 requires_expansion 提供读取参数。单条展开返回 JSON 文本片段，next_offset 用于继续，total_length 是 UTF-16 字符总量。");
        Register(context, "history.read_task_record", "read_task_record", "读取同会话的新旧任务、代理条目或完整工具调用。读取记录不会再次执行其中的工具。",
            """{"type":"object","properties":{"task_id":{"type":"string"},"agent_id":{"type":"string"},"entry_id":{"type":"string"},"tool_invocation_id":{"type":"string"},"after_sequence":{"type":"integer","minimum":0},"page_size":{"type":"integer","minimum":1,"maximum":100},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1,"maximum":65536}},"additionalProperties":false}""",
            "只选一个 agent_id、entry_id 或 tool_invocation_id；不选时读取任务状态。原生续接与审批诊断不在普通读取范围内。");
        return Task.CompletedTask;
    }

    /// <summary>读取工具没有独立后台资源。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>此插件没有私有设置，拼错或多余配置不能被静默忽略。</summary>
    public Task<ImmutableArray<PluginConfigurationError>> ValidateAsync(PluginConfigurationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.Configuration.EnumerateObject().Any()
            ? ImmutableArray.Create(new PluginConfigurationError { Path = "$", Message = "历史工具没有插件私有配置字段。" }) : ImmutableArray<PluginConfigurationError>.Empty);
    }

    /// <summary>每个工具的稳定身份独立配置权限和审批，执行器只持有自己的固定种类。</summary>
    private static void Register(IPluginContext context, string id, string name, string description, string schema, string output)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        context.Tools.Register(new AgentToolDefinition { Id = id, Name = name, Description = description, Parameters = document.RootElement, OutputDescription = output, PermissionCategory = "history.read" }, new Reader(id));
    }

    /// <summary>共享读取实现通过固定工具种类分派，不接受用户指定 SQL 或任意 JSON 路径。</summary>
    private sealed class Reader : IAgentToolHandler
    {
        private readonly string _id;
        /// <summary>固定注册身份，调用参数不能改写工具种类。</summary>
        public Reader(string id) => _id = id;

        /// <summary>完整检查所有字段类型与互斥选择，不读取数据或执行查询。</summary>
        public Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string[] allowed = _id switch
                {
                    "history.search" => ["query", "from", "until", "sender_id", "kinds", "page_size", "cursor"],
                    "history.read" => ["message_id", "from", "until", "sender_id", "page_size", "cursor", "offset", "length"],
                    _ => ["task_id", "agent_id", "entry_id", "tool_invocation_id", "after_sequence", "page_size", "offset", "length"]
                };
                Fields(arguments, allowed);
                _ = Number(arguments, "page_size", 20, 1, 100); _ = Number(arguments, "length", 8192, 1, 65536); _ = Number(arguments, "offset", 0, 0, int.MaxValue);
                _ = Number(arguments, "after_sequence", 0, 0, long.MaxValue);
                foreach (string key in new[] { "task_id", "agent_id", "entry_id", "tool_invocation_id" }) if (arguments.TryGetProperty(key, out JsonElement unused)) _ = Identifier(arguments, key);
                foreach (string key in new[] { "from", "until" }) _ = Date(arguments, key);
                if (Date(arguments, "from") > Date(arguments, "until")) throw new ArgumentException("时间范围起点不能晚于终点。");
                if (arguments.TryGetProperty("sender_id", out JsonElement sender)) _ = Text(arguments, "sender_id");
                if (_id == "history.search") { _ = Text(arguments, "query"); _ = Kinds(arguments); }
                if (arguments.TryGetProperty("message_id", out JsonElement message))
                {
                    _ = Number(arguments, "message_id", 0, 1, long.MaxValue);
                    if (arguments.EnumerateObject().Any(property => property.Name is not ("message_id" or "offset" or "length"))) throw new ArgumentException("单条消息读取不能混入列表筛选参数。");
                }
                else if (_id == "history.read" && (arguments.TryGetProperty("offset", out JsonElement offset) || arguments.TryGetProperty("length", out JsonElement length)))
                    throw new ArgumentException("聊天内容偏移必须同时指定 message_id。");
                if (_id == "history.read_task_record")
                {
                    int selectors = new[] { "agent_id", "entry_id", "tool_invocation_id" }.Count(key => arguments.TryGetProperty(key, out JsonElement unused));
                    if (selectors > 1) throw new ArgumentException("代理分页、条目和工具调用读取不能同时选择。");
                    if (arguments.TryGetProperty("agent_id", out JsonElement selectedAgent) && (arguments.TryGetProperty("offset", out JsonElement offset) || arguments.TryGetProperty("length", out JsonElement length)))
                        throw new ArgumentException("代理条目列表使用 after_sequence 分页，不能指定内容偏移。");
                    if (!arguments.TryGetProperty("agent_id", out JsonElement agent) && (arguments.TryGetProperty("after_sequence", out JsonElement after) || arguments.TryGetProperty("page_size", out JsonElement size)))
                        throw new ArgumentException("代理分页参数必须提供 agent_id。");
                }
                if (arguments.TryGetProperty("cursor", out JsonElement cursor))
                {
                    Fields(cursor, _id == "history.search" ? ["at", "id"] : ["unix_milliseconds", "message_id"]);
                    if (_id == "history.search") { if (Date(cursor, "at") is null) throw new ArgumentException("搜索游标缺少 at。"); _ = RequiredNumber(cursor, "id", 1); }
                    else { _ = RequiredNumber(cursor, "unix_milliseconds", long.MinValue); _ = RequiredNumber(cursor, "message_id", 1); }
                }
                return Task.FromResult(new PreparedToolCall { Arguments = arguments.Clone(), ExecutionDescription = "只读取当前获准会话内的已有历史，不恢复或执行记录中的动作。" });
            }
            catch (ArgumentException exception) { return Task.FromResult(new PreparedToolCall { Error = exception.Message }); }
        }

        /// <summary>通过宿主范围服务读取实际记录，较大输出返回显式分页或展开引用。</summary>
        public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
        {
            JsonElement args = prepared.Arguments!.Value;
            int size = (int)Number(args, "page_size", 20, 1, 100);
            if (_id == "history.search")
            {
                HistorySearchCursor? cursor = args.TryGetProperty("cursor", out JsonElement value) ? new HistorySearchCursor { At = Date(value, "at")!.Value, Id = RequiredNumber(value, "id", 1) } : null;
                HistorySearchPage page = await context.Search.SearchAsync(new HistorySearchQuery
                { Conversation = context.Conversation, Text = Text(args, "query"), SenderId = OptionalText(args, "sender_id"), From = Date(args, "from"), Until = Date(args, "until"), Kinds = Kinds(args), PageSize = size, Before = cursor }, cancellationToken).ConfigureAwait(false);
                return Page(page.Hits, page.Next is null ? null : SearchCursor(page.Next.At, page.Next.Id), hit => SearchCursor(hit.At, hit.Id), SearchReference, context.MaximumResultBytes);
            }
            if (_id == "history.read")
            {
                if (args.TryGetProperty("message_id", out JsonElement requested))
                {
                    MessageSnapshot? message = await context.History.GetAsync(requested.GetInt64(), cancellationToken).ConfigureAwait(false);
                    return message is null ? Missing() : Chunk(message, args, context.MaximumResultBytes);
                }
                HistoryCursor? cursor = args.TryGetProperty("cursor", out JsonElement value) ? new HistoryCursor { UnixMilliseconds = value.GetProperty("unix_milliseconds").GetInt64(), MessageId = value.GetProperty("message_id").GetInt64() } : null;
                HistoryPage page = await context.History.QueryAsync(new HistoryQuery
                {
                    Address = context.Conversation,
                    Time = HistoryTime.Received,
                    From = Date(args, "from"),
                    Until = Date(args, "until"),
                    SenderId = OptionalText(args, "sender_id"),
                    IncludeRecalled = true,
                    IncludeDeleted = true,
                    IncludeUnconfirmed = false,
                    PageDirection = PageDirection.Backward,
                    Cursor = cursor,
                    PageSize = size
                }, cancellationToken).ConfigureAwait(false);
                return Page(page.Messages, page.NextCursor is null ? null : ChatCursor(page.NextCursor.UnixMilliseconds, page.NextCursor.MessageId),
                    message => ChatCursor(message.ReceivedAt.ToUnixTimeMilliseconds(), message.Id), message => Reference("read_history", "message_id", message.Id), context.MaximumResultBytes);
            }
            Guid? taskId = args.TryGetProperty("task_id", out JsonElement task) ? task.GetGuid() : null;
            if (args.TryGetProperty("tool_invocation_id", out JsonElement invocation))
            {
                AgentToolRecord? record = await context.ToolHistory.GetToolCallAsync(context.Conversation, invocation.GetGuid(), cancellationToken).ConfigureAwait(false);
                return record is null || taskId.HasValue && record.Origin.TaskId != taskId ? Missing() : Chunk(record, args, context.MaximumResultBytes);
            }
            if (args.TryGetProperty("entry_id", out JsonElement entryId))
            {
                AgentEntry? entry = await context.AgentHistory.GetEntryAsync(context.Conversation, entryId.GetGuid(), cancellationToken).ConfigureAwait(false);
                return entry is null || taskId.HasValue && entry.TaskId != taskId ? Missing() : Chunk(entry, args, context.MaximumResultBytes);
            }
            Guid selectedTask = taskId ?? context.Origin.TaskId ?? throw new ArgumentException("当前调用没有任务身份，必须指定 task_id。");
            if (args.TryGetProperty("agent_id", out JsonElement agentId))
            {
                AgentEntryPage page = await context.AgentHistory.ReadEntriesAsync(context.Conversation, selectedTask, agentId.GetGuid(), Number(args, "after_sequence", 0, 0, long.MaxValue), size, cancellationToken).ConfigureAwait(false);
                return Page(page.Entries, page.NextSequence, entry => entry.Sequence, entry => Reference("read_task_record", "entry_id", entry.Id), context.MaximumResultBytes);
            }
            AgentTaskSnapshot? summary = await context.AgentHistory.GetTaskAsync(context.Conversation, selectedTask, cancellationToken).ConfigureAwait(false);
            return summary is null ? Missing() : Chunk(new Dictionary<string, object>
            {
                ["task"] = summary,
                ["agents"] = await context.AgentHistory.GetAgentsAsync(context.Conversation, selectedTask, cancellationToken).ConfigureAwait(false)
            }, args, context.MaximumResultBytes);
        }
    }

    /// <summary>使用共享 JSON 分页器，私有类型不进入宿主序列化缓存。</summary>
    private static ToolExecutionResult Chunk<T>(T record, JsonElement args, int maximum)
        => ToolResultPages.Json(JsonSerializer.SerializeToElement(record, JsonOptions), (int)Number(args, "offset", 0, 0, int.MaxValue),
            (int)Number(args, "length", 8192, 1, 65536), maximum);

    /// <summary>列表不静默删除大记录；超限的单条给出展开工具与身份，游标继续剩余条目。</summary>
    private static ToolExecutionResult Page<T>(ImmutableArray<T> items, object? originalNext, Func<T, object> cursor, Func<T, object> reference, int maximum)
    {
        int count = items.Length;
        bool expansion = false;
        while (true)
        {
            object[] values = expansion ? [reference(items[0])] : items.Take(count).Cast<object>().ToArray();
            object? next = count < items.Length && count > 0 ? cursor(items[count - 1]) : originalNext;
            JsonElement data = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["items"] = values, ["next"] = next, ["limited_by_output_budget"] = count < items.Length || expansion }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(data.GetRawText()) <= maximum - 1024) return new ToolExecutionResult { State = ToolCallState.Succeeded, Data = data };
            if (count > 1) { count--; continue; }
            if (count == 1 && !expansion) { expansion = true; continue; }
            return new ToolExecutionResult { State = ToolCallState.Failed, Error = "模型结果预算不足以容纳读取引用。", ErrorKind = "OutputLimit" };
        }
    }

    /// <summary>来源种类决定公开读取入口，不构造任意文件路径。</summary>
    private static object SearchReference(HistorySearchHit hit) => hit.Kind == HistoryRecordKind.Chat ? Reference("read_history", "message_id", hit.MessageId!)
        : Reference("read_task_record", hit.Kind == HistoryRecordKind.ToolText ? "tool_invocation_id" : "entry_id", hit.SourceId);
    /// <summary>大记录引用明确表示需要单独展开。</summary>
    private static object Reference(string tool, string key, object id) => new Dictionary<string, object> { ["requires_expansion"] = true, ["tool"] = tool, ["arguments"] = new Dictionary<string, object> { [key] = id } };
    /// <summary>搜索游标使用时间与投影身份。</summary>
    private static object SearchCursor(DateTimeOffset at, long id) => new Dictionary<string, object> { ["at"] = at, ["id"] = id };
    /// <summary>聊天游标使用原始接收时间与消息身份。</summary>
    private static object ChatCursor(long milliseconds, long id) => new Dictionary<string, object> { ["unix_milliseconds"] = milliseconds, ["message_id"] = id };
    /// <summary>越界身份与不存在采用相同结果，不泄露其他会话记录。</summary>
    private static ToolExecutionResult Missing() => new() { State = ToolCallState.Rejected, Error = "当前获准范围内没有该记录。", ErrorKind = "NotFound" };

    /// <summary>检查顶层及游标字段，不自动修复拼写或覆盖重复值。</summary>
    private static void Fields(JsonElement json, string[] names)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("历史工具参数必须是对象。");
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (JsonProperty property in json.EnumerateObject()) if (!found.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("重复或未知参数：" + property.Name);
    }
    /// <summary>字符串必须实际存在且非空，不转换其他 JSON 类型。</summary>
    private static string Text(JsonElement json, string name) => json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new ArgumentException(name + " 必须是非空字符串。");
    /// <summary>省略的可选字符串保持空值。</summary>
    private static string? OptionalText(JsonElement json, string name) => json.TryGetProperty(name, out JsonElement unused) ? Text(json, name) : null;
    /// <summary>记录身份必须是非空 Guid，不进行模糊匹配。</summary>
    private static Guid Identifier(JsonElement json, string name) => Guid.TryParse(Text(json, name), out Guid id) && id != Guid.Empty ? id : throw new ArgumentException(name + " 不是有效身份。");
    /// <summary>严格整数与范围校验，数字字符串不是整数。</summary>
    private static long Number(JsonElement json, string name, long fallback, long minimum, long maximum)
    {
        if (!json.TryGetProperty(name, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= minimum && number <= maximum ? number : throw new ArgumentException(name + " 必须是允许范围内的整数。");
    }
    /// <summary>游标必填整数没有默认值。</summary>
    private static long RequiredNumber(JsonElement json, string name, long minimum) => json.TryGetProperty(name, out JsonElement unused) ? Number(json, name, 0, minimum, long.MaxValue) : throw new ArgumentException("缺少 " + name);
    /// <summary>时间必须显式带 UTC 或偏移，不借用运行机器的本地时区。</summary>
    private static DateTimeOffset? Date(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " 必须是带偏移的时间字符串。");
        string text = value.GetString()!;
        return Regex.IsMatch(text, "T.*(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time) ? time : throw new ArgumentException(name + " 必须是带偏移的有效时间。");
    }
    /// <summary>记录种类是固定枚举，不把用户字符串作为查询表达式。</summary>
    private static ImmutableArray<HistoryRecordKind> Kinds(JsonElement json)
    {
        if (!json.TryGetProperty("kinds", out JsonElement values)) return [];
        if (values.ValueKind != JsonValueKind.Array) throw new ArgumentException("kinds 必须是字符串数组。");
        return values.EnumerateArray().Select(value => value.ValueKind != JsonValueKind.String ? throw new ArgumentException("记录种类必须是字符串。") : value.GetString() switch
        { "chat" => HistoryRecordKind.Chat, "agent_input" => HistoryRecordKind.AgentInput, "agent_result" => HistoryRecordKind.AgentResult, "tool_text" => HistoryRecordKind.ToolText, _ => throw new ArgumentException("未知记录种类。") }).ToImmutableArray();
    }
}
