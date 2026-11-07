using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>内置协作工具直接操作宿主任务树，投递、唤醒与停止分别表达。</summary>
public sealed partial class AgentRuntime
{
    /// <summary>真实控制工具统一提供严格 Schema，等待时长没有默认值。</summary>
    private static ImmutableArray<AgentToolDefinition> Definitions() =>
    [
        Definition(AgentToolIds.Spawn, "spawn_agent", "按用户模板创建并启动子代理，返回后子代理独立并行工作。",
            """{"type":"object","properties":{"task":{"type":"string"},"template":{"type":"string"},"references":{"type":"array","items":{"type":"object","properties":{"agent_id":{"type":"string"},"entry_sequence":{"type":"integer","minimum":1}},"required":["agent_id","entry_sequence"],"additionalProperties":false}}},"required":["task"],"additionalProperties":false}"""),
        Definition(AgentToolIds.List, "list_agents", "查看同任务树中的代理活动、成果修订和实际工作。", """{"type":"object","properties":{},"additionalProperties":false}"""),
        Definition(AgentToolIds.Message, "send_agent_message", "向同任务代理投递信息，空闲接收者不会仅因本消息启动。",
            """{"type":"object","properties":{"agent_id":{"type":"string"},"message":{"type":"string"}},"required":["agent_id","message"],"additionalProperties":false}"""),
        Definition(AgentToolIds.Followup, "followup_agent", "向同任务代理追加工作；空闲时唤醒，运行时在安全边界消费。",
            """{"type":"object","properties":{"agent_id":{"type":"string"},"task":{"type":"string"}},"required":["agent_id","task"],"additionalProperties":false}"""),
        Definition(AgentToolIds.Wait, "wait_agents", "有期限地等待同任务代理成果或状态变化；超时只结束本次等待。",
            """{"type":"object","properties":{"agent_ids":{"type":"array","minItems":1,"items":{"type":"string"}},"timeout_seconds":{"type":"number","minimum":1,"maximum":3600},"after_revisions":{"type":"object","additionalProperties":{"type":"integer","minimum":0}}},"required":["agent_ids","timeout_seconds"],"additionalProperties":false}"""),
        Definition(AgentToolIds.Stop, "stop_agent", "请求自己的后代子树优雅停止，兄弟代理不能互相停止。",
            """{"type":"object","properties":{"agent_id":{"type":"string"}},"required":["agent_id"],"additionalProperties":false}"""),
        Definition(AgentToolIds.FinishTurn, "finish_turn", "结束本轮且不新增公开回复；同组其他工具收尾后生效，不取消子工作。",
            """{"type":"object","properties":{"reason":{"type":"string"}},"additionalProperties":false}"""),
        AssetDefinition(AgentToolIds.ReadContent, "read_content", "读取同会话已有引用、转发节点及局部失败；默认立即返回已取得部分，wait=true 等待已有归档。",
            """{"type":"object","properties":{"content_id":{"type":"string"},"wait":{"type":"boolean"},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1,"maximum":65536}},"required":["content_id"],"additionalProperties":false}"""),
        AssetDefinition(AgentToolIds.ReadTranscription, "read_transcription", "读取同会话图片或语音的成功转述。generate=true 明确执行缺少的转述；refresh=true 要求独立的 media.transcription.refresh 权限。",
            """{"type":"object","properties":{"media_id":{"type":"string"},"generate":{"type":"boolean"},"refresh":{"type":"boolean"},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1,"maximum":65536}},"required":["media_id"],"additionalProperties":false}""")
    ];

    /// <summary>控制工具不使用普通工作槽，描述与 JSON 都是宿主共享值。</summary>
    private static AgentToolDefinition Definition(string id, string name, string description, string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return new AgentToolDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Parameters = document.RootElement.Clone(),
            OutputDescription = "实际接单、投递、等待或控制结果及稳定身份。",
            PermissionCategory = "agent.control",
            IsControl = true,
            ExecutionTimeout = null
        };
    }

    /// <summary>控制准备阶段只验证参数、身份及资料引用，所有状态变更留在执行阶段。</summary>
    public async Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            string tool = arguments.GetProperty("tool").GetString()!;
            JsonElement input = arguments.GetProperty("arguments");
            if (tool is AgentToolIds.ReadContent or AgentToolIds.ReadTranscription) return await PrepareAssetAsync(context, tool, input, cancellationToken).ConfigureAwait(false);
            switch (tool)
            {
                case AgentToolIds.Spawn:
                    Fields(input, ["task", "template", "references"]); RequiredText(input, "task");
                    if (input.TryGetProperty("template", out JsonElement template)) RequiredText(input, "template");
                    break;
                case AgentToolIds.Message:
                    Fields(input, ["agent_id", "message"]); AgentId(input, "agent_id"); RequiredText(input, "message"); break;
                case AgentToolIds.Followup:
                    Fields(input, ["agent_id", "task"]); AgentId(input, "agent_id"); RequiredText(input, "task"); break;
                case AgentToolIds.Stop:
                    Fields(input, ["agent_id"]); AgentId(input, "agent_id"); break;
                case AgentToolIds.Wait:
                    Fields(input, ["agent_ids", "timeout_seconds", "after_revisions"]);
                    Guid[] ids = WaitIds(input);
                    if (!input.TryGetProperty("timeout_seconds", out JsonElement timeout) || timeout.ValueKind != JsonValueKind.Number ||
                        !timeout.TryGetDouble(out double seconds) || !double.IsFinite(seconds) || seconds < 1 || seconds > 3600)
                        throw new ArgumentException("timeout_seconds 必须显式提供 1～3600 之间的有限数值。");
                    if (input.TryGetProperty("after_revisions", out JsonElement revisions))
                    {
                        if (revisions.ValueKind != JsonValueKind.Object) throw new ArgumentException("after_revisions 必须是对象。");
                        foreach (JsonProperty property in revisions.EnumerateObject())
                            if (!Guid.TryParse(property.Name, out Guid target) || !ids.Contains(target) || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out long revision) || revision < 0)
                                throw new ArgumentException("成果修订必须引用等待目标并使用非负整数。");
                    }
                    break;
                case AgentToolIds.List: Fields(input, []); break;
                case AgentToolIds.FinishTurn:
                    Fields(input, ["reason"]);
                    if (input.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind != JsonValueKind.String) throw new ArgumentException("reason 必须是字符串。");
                    break;
                default: throw new ArgumentException("框架工具身份无效。");
            }
            await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (Tree tree, Node caller) = Caller(context);
                if (tool == AgentToolIds.Spawn)
                {
                    string selected = input.TryGetProperty("template", out JsonElement template) ? template.GetString()! : context.Profile.Profile.Agent.DefaultTemplate ?? throw new ArgumentException("没有配置默认子代理模板。");
                    if (!tree.Task.InitialProfile.Templates.Any(item => item.Id == selected) || !context.Profile.Profile.Agent.Templates.Any(item => item.Id == selected))
                        throw new ArgumentException("所选模板不在该任务允许的用户模板中。");
                    SubagentTemplate settings = context.Profile.Profile.Agent.Templates.Single(item => item.Id == selected);
                    if (!HasProvider(context.Profile, settings.Models) || settings.Tools.Any(id => !_capabilities.CaptureTools().Any(registered => registered.Definition.Id == id)))
                        throw new ArgumentException("子代理模板所需的提供商或工具尚未加载。");
                    ImmutableArray<JsonElement> materials = await ReferencesAsync(tree, input, cancellationToken).ConfigureAwait(false);
                    input = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["task"] = RequiredText(input, "task"), ["template"] = selected, ["materials"] = materials });
                }
                else if (tool is AgentToolIds.Message or AgentToolIds.Followup or AgentToolIds.Stop)
                {
                    Guid target = AgentId(input, "agent_id");
                    if (!tree.Nodes.ContainsKey(target)) throw new ArgumentException("目标代理不属于当前任务树。");
                    if (tool == AgentToolIds.Stop && !Descendant(tree, target, caller.Identity.Id)) throw new ArgumentException("只能停止自己的后代子树。");
                }
                else if (tool == AgentToolIds.Wait && WaitIds(input).Any(id => !tree.Nodes.ContainsKey(id))) throw new ArgumentException("等待目标必须全部属于同一任务树。");
                return new PreparedToolCall { Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["tool"] = tool, ["arguments"] = input }), ExecutionDescription = "在当前任务树内执行 " + tool + "，不扩大接收者的权限。" };
            }
            finally { _coordination.Release(); }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new PreparedToolCall { Error = exception.Message }; }
    }

    /// <summary>执行一次已经准备的控制动作，等待独立于普通插件执行槽。</summary>
    public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        JsonElement actual = prepared.Arguments!.Value;
        string tool = actual.GetProperty("tool").GetString()!;
        JsonElement input = actual.GetProperty("arguments");
        if (tool is AgentToolIds.ReadContent or AgentToolIds.ReadTranscription) return await ReadAssetAsync(context, tool, input, cancellationToken).ConfigureAwait(false);
        if (tool == AgentToolIds.Wait) return await WaitAsync(context, input, cancellationToken).ConfigureAwait(false);
        await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (Tree tree, Node caller) = Caller(context);
            if (caller.StopDeadline.HasValue || caller.Closed || tree.Closing) return Rejected("调用者已停止。");
            switch (tool)
            {
                case AgentToolIds.Spawn:
                    string template = input.GetProperty("template").GetString()!;
                    if (!_profiles.Resolve(tree.Task.Conversation).Profile.Agent.Templates.Any(item => item.Id == template)) return Rejected("所选模板已经撤销。");
                    AgentSnapshot? child = await _store.CreateChildAsync(caller.Identity.Id, template, RequiredText(input, "task"), input, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    if (child is null) return Rejected("无法创建子代理：任务已停止或累计数量、深度预算不足。");
                    Node node = new(child, tree.Task.Conversation, _windows, _history, _stop.Token);
                    node.Inputs.Enqueue(new Input(null, JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["kind"] = "parent_assignment", ["sender_agent_id"] = caller.Identity.Id, ["content"] = input }), 0));
                    tree.Nodes.Add(child.Id, node); StartUnderLock(tree, node);
                    return Value(new Dictionary<string, object> { ["agent_id"] = child.Id, ["task_id"] = tree.Task.Id, ["template"] = template });
                case AgentToolIds.List:
                    ImmutableArray<AgentSnapshot> visible = await context.AgentHistory.GetAgentsAsync(tree.Task.Conversation, tree.Task.Id, cancellationToken).ConfigureAwait(false);
                    return Value(new Dictionary<string, object>
                    {
                        ["agents"] = visible.Select(agent => new Dictionary<string, object?>
                        {
                            ["agent_id"] = agent.Id,
                            ["parent_agent_id"] = agent.ParentId,
                            ["template"] = agent.TemplateId,
                            ["activity"] = agent.Activity.ToString(),
                            ["result_revision"] = agent.ResultRevision,
                            ["stop_deadline"] = agent.StopDeadline,
                            ["exited_at"] = agent.ExitedAt
                        }),
                        ["active_work"] = tree.Working.ActiveCount
                    });
                case AgentToolIds.Message:
                case AgentToolIds.Followup:
                    Node recipient = tree.Nodes[AgentId(input, "agent_id")];
                    string text = RequiredText(input, tool == AgentToolIds.Message ? "message" : "task");
                    int bytes = Encoding.UTF8.GetByteCount(text);
                    if (recipient.Closed || recipient.StopDeadline.HasValue) return Rejected("接收者已停止。");
                    if (bytes > _options.MaximumInputBytes || !HasMailboxCapacity(recipient, bytes)) return Rejected("代理消息或接收邮箱超过容量。");
                    Guid mailId = Guid.NewGuid();
                    JsonElement mail = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        ["message_id"] = mailId,
                        ["task_id"] = tree.Task.Id,
                        ["sender_agent_id"] = caller.Identity.Id,
                        ["recipient_agent_id"] = recipient.Identity.Id,
                        ["kind"] = tool == AgentToolIds.Message ? "communication" : "followup",
                        ["text"] = text
                    });
                    AgentEntry saved = await _store.AppendEntryAsync(recipient.Identity.Id, tool == AgentToolIds.Message ? AgentEntryKind.AgentMessage : AgentEntryKind.Followup, mail, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    Input item = new(null, mail, bytes);
                    if (tool == AgentToolIds.Message) { recipient.Mail.Enqueue(item); recipient.RunningMail |= recipient.InTurn; }
                    else recipient.Inputs.Enqueue(item);
                    recipient.MailboxBytes += bytes; recipient.Signal.Pulse();
                    if (tool == AgentToolIds.Followup) StartUnderLock(tree, recipient);
                    return Value(new Dictionary<string, object> { ["message_id"] = mailId, ["entry_sequence"] = saved.Sequence, ["wakes_idle_agent"] = tool == AgentToolIds.Followup });
                case AgentToolIds.Stop:
                    Guid target = AgentId(input, "agent_id");
                    if (!Descendant(tree, target, caller.Identity.Id)) return Rejected("只能停止自己的后代子树。");
                    await StopUnderLockAsync(tree, tree.Nodes[target], false, cancellationToken).ConfigureAwait(false);
                    return Value(new Dictionary<string, object> { ["agent_id"] = target, ["stop_requested"] = true });
                case AgentToolIds.FinishTurn:
                    caller.FinishTurn = true;
                    return Value(new Dictionary<string, object> { ["finished_turn"] = true });
                default: return Rejected("控制工具不存在。");
            }
        }
        finally { _coordination.Release(); }
    }

    /// <summary>每次等待有固定截止时间；信号、超时和取消竞争只返回一个结果，不取消目标。</summary>
    private async Task<ToolExecutionResult> WaitAsync(AgentToolContext context, JsonElement input, CancellationToken token)
    {
        Guid[] ids = WaitIds(input);
        TimeSpan timeout = TimeSpan.FromSeconds(input.GetProperty("timeout_seconds").GetDouble());
        long start = _clock.GetTimestamp();
        Dictionary<Guid, long> baseline = [];
        Node? waiter = null;
        try
        {
            while (true)
            {
                Task changed;
                await _coordination.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    (Tree tree, Node caller) = Caller(context);
                    if (waiter is null)
                    {
                        waiter = caller; waiter.Waiters++;
                        await ActivityAsync(caller, AgentActivity.Waiting).ConfigureAwait(false);
                        foreach (Guid id in ids) baseline[id] = caller.Consumed.GetValueOrDefault(id);
                        if (input.TryGetProperty("after_revisions", out JsonElement revisions))
                            foreach (JsonProperty property in revisions.EnumerateObject())
                            {
                                // 模型提供的旧游标不能回退宿主已经消费的位置，避免同一成果再次进入工具结果。
                                Guid target = Guid.Parse(property.Name);
                                baseline[target] = Math.Max(baseline[target], property.Value.GetInt64());
                            }
                    }
                    if (caller.StopDeadline.HasValue || tree.Closing) return WaitResult("stopping", []);
                    List<Dictionary<string, object?>> results = [];
                    foreach (Guid id in ids)
                    {
                        Node target = tree.Nodes[id];
                        foreach (KeyValuePair<long, AgentResult> result in target.Results.Where(result => result.Key > baseline[id]).OrderBy(result => result.Key))
                        {
                            results.Add(new Dictionary<string, object?> { ["agent_id"] = id, ["revision"] = result.Key, ["result"] = result.Value });
                            caller.Consumed[id] = Math.Max(caller.Consumed.GetValueOrDefault(id), result.Key);
                        }
                        if (target.Closed && target.Revision <= baseline[id]) results.Add(new Dictionary<string, object?> { ["agent_id"] = id, ["revision"] = target.Revision, ["closed"] = true });
                    }
                    if (results.Count > 0) return WaitResult("result", results);
                    if (caller.Inputs.Count > 0) return WaitResult("new_input", []);
                    if (caller.Mail.Count > 0) return WaitResult("communication", []);
                    changed = caller.Signal.Changed;
                }
                finally { _coordination.Release(); }
                TimeSpan remaining = timeout - _clock.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero) return WaitResult("timeout", []);
                try { await changed.WaitAsync(remaining, _clock, token).ConfigureAwait(false); }
                catch (TimeoutException) { return WaitResult("timeout", []); }
            }
        }
        finally
        {
            if (waiter is not null)
            {
                await _coordination.WaitAsync().ConfigureAwait(false);
                try { waiter.Waiters--; await ActivityAsync(waiter, AgentActivity.RunningTool).ConfigureAwait(false); }
                finally { _coordination.Release(); }
            }
        }
    }

    /// <summary>消费位置只在安全边界推进，等待工具已经返回的成果不会再次插入模型。</summary>
    private void ConsumeUnderLock(Tree tree, Node node)
    {
        while (node.Inputs.TryDequeue(out Input? input))
        {
            node.MailboxBytes -= input.Bytes;
            if (input.User is not null) node.Conversation.Accept(input.User);
            else node.Conversation.Append(new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock(input.Data!.Value.GetRawText())] });
        }
        while (node.Mail.TryDequeue(out Input? mail))
        {
            node.MailboxBytes -= mail.Bytes;
            node.Conversation.Append(new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock(mail.Data!.Value.GetRawText())] });
        }
        node.RunningMail = false;
        foreach ((Guid agent, long revision) in node.Notices)
        {
            if (revision <= node.Consumed.GetValueOrDefault(agent)) continue;
            AgentResult result = tree.Nodes[agent].Results[revision];
            node.Conversation.Append(new ModelMessage
            {
                Role = "user",
                Content = [ModelBlock.TextBlock(JsonSerializer.Serialize(new Dictionary<string, object>
            { ["kind"] = "agent_result", ["agent_id"] = agent, ["revision"] = revision, ["result"] = result }))]
            });
            node.Consumed[agent] = revision;
        }
        node.Notices.Clear();
    }

    /// <summary>有新成果才唤醒；普通通信本身不会启动空闲代理。</summary>
    private static bool HasUnconsumedResults(Node node) => node.Notices.Any(notice => notice.Revision > node.Consumed.GetValueOrDefault(notice.Agent));

    /// <summary>按宿主绑定身份定位调用者，模型参数不能指定另一任务树。</summary>
    private (Tree Tree, Node Node) Caller(AgentToolContext context)
    {
        if (!_trees.TryGetValue(context.Conversation, out Tree? tree) || tree.Task.Id != context.Origin.TaskId || context.Origin.AgentId is not Guid agent || !tree.Nodes.TryGetValue(agent, out Node? node))
            throw new ArgumentException("调用者不属于活动任务树。");
        return (tree, node);
    }

    /// <summary>资料引用只接受同树的可展示成果，签名和原生推理不能传给另一个代理。</summary>
    private async Task<ImmutableArray<JsonElement>> ReferencesAsync(Tree tree, JsonElement input, CancellationToken token)
    {
        if (!input.TryGetProperty("references", out JsonElement references)) return [];
        if (references.ValueKind != JsonValueKind.Array) throw new ArgumentException("references 必须是数组。");
        ImmutableArray<JsonElement>.Builder results = ImmutableArray.CreateBuilder<JsonElement>();
        foreach (JsonElement reference in references.EnumerateArray())
        {
            Fields(reference, ["agent_id", "entry_sequence"]);
            Guid agent = AgentId(reference, "agent_id");
            if (!tree.Nodes.ContainsKey(agent) || !reference.TryGetProperty("entry_sequence", out JsonElement sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out long position) || position < 1)
                throw new ArgumentException("资料引用不属于本树或记录序号无效。");
            AgentEntryPage page = await _store.ReadEntriesAsync(tree.Task.Conversation, tree.Task.Id, agent, position - 1, 1, token).ConfigureAwait(false);
            AgentEntry? entry = page.Entries.SingleOrDefault();
            if (entry is null || entry.Sequence != position || entry.Kind != AgentEntryKind.Result) throw new ArgumentException("所选资料不是可展示的成果记录。");
            results.Add(entry.Data.Clone());
        }
        return results.ToImmutable();
    }

    /// <summary>沿父链判断祖先权，不根据角色名或模型自称扩大停止范围。</summary>
    private static bool Descendant(Tree tree, Guid candidate, Guid ancestor)
    {
        Node node = tree.Nodes[candidate];
        while (node.Identity.ParentId is Guid parent)
        { if (parent == ancestor) return true; node = tree.Nodes[parent]; }
        return false;
    }

    /// <summary>未知字段和重复字段都直接拒绝，不尝试删除参数再执行。</summary>
    private static void Fields(JsonElement input, string[] allowed)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("控制参数必须是对象。");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in input.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("控制参数包含重复或未知字段：" + property.Name);
    }

    /// <summary>要求文本不能用空值或数字替代。</summary>
    private static string RequiredText(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException(name + " 必须是非空字符串。");
        return value.GetString()!;
    }

    /// <summary>代理身份采用稳定 Guid，不接受模糊名称匹配。</summary>
    private static Guid AgentId(JsonElement input, string name) => Guid.TryParse(RequiredText(input, name), out Guid value) ? value : throw new ArgumentException(name + " 不是代理身份。");

    /// <summary>等待集合显式提供且不含重复身份。</summary>
    private static Guid[] WaitIds(JsonElement input)
    {
        if (!input.TryGetProperty("agent_ids", out JsonElement array) || array.ValueKind != JsonValueKind.Array) throw new ArgumentException("agent_ids 必须是非空数组。");
        Guid[] values = array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out Guid id) ? id : throw new ArgumentException("等待目标身份无效。")).ToArray();
        if (values.Length == 0 || values.Distinct().Count() != values.Length) throw new ArgumentException("等待目标不能为空或重复。");
        return values;
    }

    /// <summary>成功控制结果只返回共享业务数据。</summary>
    private static ToolExecutionResult Value(object data) => new() { State = ToolCallState.Succeeded, Data = JsonSerializer.SerializeToElement(data) };
    /// <summary>控制拒绝没有执行对应动作。</summary>
    private static ToolExecutionResult Rejected(string reason) => new() { State = ToolCallState.Rejected, Error = reason, ErrorKind = "Control" };
    /// <summary>等待的返回原因与成果分开，超时不改变任何目标状态。</summary>
    private static ToolExecutionResult WaitResult(string reason, List<Dictionary<string, object?>> results)
        => Value(new Dictionary<string, object> { ["reason"] = reason, ["results"] = results });
}
