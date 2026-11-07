using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>Agent 的身份、预算与审计写入复用同一有限单写执行器。</summary>
public sealed partial class SqliteStore : IAgentStore
{
    /// <summary>按规范消息身份查找登记唤醒事实，已有任务结束后仍能识别重复触发。</summary>
    public Task<AgentWakeResult?> FindWakeAsync(long messageId, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        MessageEntity? source = database.Messages.SingleOrDefault(message => message.Id == messageId);
        if (source is null) return null;
        long canonical = ResolveCanonical(database, source).Id;
        AgentEntryEntity? entry = database.AgentEntries.AsNoTracking().SingleOrDefault(item => item.SourceMessageId == canonical);
        return entry is null ? null : new AgentWakeResult { State = AgentWakeState.Duplicate, TaskId = entry.TaskId, AgentId = entry.AgentId, EntrySequence = entry.Sequence };
    }, cancellationToken);

    /// <summary>聊天身份核验、去重、唯一主代理占有和输入保存共同提交。</summary>
    public Task<AgentWakeResult> RegisterWakeAsync(AgentWakeAdmission request, CancellationToken cancellationToken)
    {
        ContentValidation.Validate(request.WakeRequest.CurrentContent);
        request.WakeRequest.Cause.Validate();
        AgentWakeRequest wake = request.WakeRequest with
        { CurrentContent = request.WakeRequest.CurrentContent.Select(segment => segment.Freeze()).ToImmutableArray() };
        if (request.NewTaskId == Guid.Empty || request.NewRootAgentId == Guid.Empty)
            throw new ArgumentException("Agent 运行身份无效。");
        return WriteAsync(database =>
        {
            MessageEntity? original = database.Messages.SingleOrDefault(message => message.Id == wake.MessageId);
            if (original is null || original.Direction != MessageDirection.Incoming || original.SenderId == original.BotAccountId)
                return new AgentWakeResult { State = AgentWakeState.Rejected, Error = "登记唤醒必须引用已保存的用户消息。" };
            original = ResolveCanonical(database, original);
            AgentEntryEntity? duplicate = database.AgentEntries.SingleOrDefault(entry => entry.SourceMessageId == original.Id);
            if (duplicate is not null)
                return new AgentWakeResult { State = AgentWakeState.Duplicate, TaskId = duplicate.TaskId, AgentId = duplicate.AgentId, EntrySequence = duplicate.Sequence };
            string conversation = JsonSerializer.Serialize(Address(original));
            AgentTaskEntity? task = database.AgentTasks.SingleOrDefault(item => item.ConversationKey == conversation && item.ReleasedAt == null);
            if (task is not null && (task.State != AgentTaskState.Active || task.RunId != request.RunId))
                return new AgentWakeResult { State = AgentWakeState.Busy, TaskId = task.Id, AgentId = task.RootAgentId, Error = "当前会话的主代理尚未实际退出。" };
            bool created = task is null;
            AgentSessionEntity agent;
            if (task is null)
            {
                task = new AgentTaskEntity
                {
                    Id = request.NewTaskId,
                    RootAgentId = request.NewRootAgentId,
                    RunId = request.RunId,
                    ConversationKey = conversation,
                    TriggerSenderId = original.SenderId,
                    ProfileId = request.Profile.Profile.Id,
                    ConfigurationRevision = request.Profile.Revision,
                    InitialProfileJson = JsonSerializer.Serialize(request.Profile.Profile.Agent),
                    CreatedAt = request.Now.ToUnixTimeMilliseconds(),
                    State = AgentTaskState.Active,
                    TotalAgents = 1
                };
                agent = new AgentSessionEntity
                {
                    Id = request.NewRootAgentId,
                    TaskId = task.Id,
                    CreatedAt = task.CreatedAt,
                    Activity = AgentActivity.Ready
                };
                task.Agents.Add(agent);
                database.AgentTasks.Add(task);
            }
            else agent = database.AgentSessions.Single(item => item.Id == task.RootAgentId);
            AgentWakeInput input = new()
            {
                MessageId = original.Id,
                SenderId = original.SenderId,
                ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(original.ReceivedAt),
                OccurredAt = original.OccurredAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(original.OccurredAt.Value) : null,
                Content = wake.CurrentContent,
                Cause = wake.Cause
            };
            AgentEntryEntity entry = AddAgentEntry(database, agent, AgentEntryKind.WakeInput, JsonSerializer.Serialize(input), request.Now);
            entry.SourceMessageId = original.Id;
            database.SaveChanges();
            return new AgentWakeResult
            {
                State = created ? AgentWakeState.Created : AgentWakeState.Queued,
                TaskId = task.Id,
                AgentId = agent.Id,
                EntrySequence = entry.Sequence
            };
        }, cancellationToken);
    }

    /// <summary>完整会话键使不同账号、平台和适配器不会共享主代理。</summary>
    public Task<AgentTaskSnapshot?> GetActiveTaskAsync(MessageAddress conversation, CancellationToken cancellationToken)
    {
        conversation.Validate();
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database =>
        {
            AgentTaskEntity? task = database.AgentTasks.AsNoTracking().SingleOrDefault(item => item.ConversationKey == key && item.ReleasedAt == null);
            return task is null ? null : AgentTaskSnapshotOf(task);
        }, cancellationToken);
    }

    /// <summary>指定会话外的身份返回不存在，不提供按任意任务标识越界读取。</summary>
    public Task<AgentTaskSnapshot?> GetTaskAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database =>
        {
            AgentTaskEntity? task = database.AgentTasks.AsNoTracking().SingleOrDefault(item => item.Id == taskId && item.ConversationKey == key);
            return task is null ? null : AgentTaskSnapshotOf(task);
        }, cancellationToken);
    }

    /// <summary>仅返回同一允许会话内的代理，排序不依赖字典或加载顺序。</summary>
    public Task<ImmutableArray<AgentSnapshot>> GetAgentsAsync(MessageAddress conversation, Guid taskId, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database => database.AgentSessions.AsNoTracking().Where(agent => agent.TaskId == taskId &&
            database.AgentTasks.Any(task => task.Id == taskId && task.ConversationKey == key))
            .OrderBy(agent => agent.CreatedAt).ThenBy(agent => agent.Id).ToList().Select(AgentSnapshotOf).ToImmutableArray(), cancellationToken);
    }

    /// <summary>按条目稳定身份读取，并在数据库内核对完整会话归属。</summary>
    public Task<AgentEntry?> GetEntryAsync(MessageAddress conversation, Guid entryId, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database =>
        {
            AgentEntryEntity? entry = database.AgentEntries.AsNoTracking().SingleOrDefault(item => item.Id == entryId && database.AgentTasks.Any(task => task.Id == item.TaskId && task.ConversationKey == key));
            return entry is null ? null : AgentEntrySnapshotOf(entry);
        }, cancellationToken);
    }

    /// <summary>单代理序号游标对应稳定顺序，有限页不会读取整棵任务树。</summary>
    public Task<AgentEntryPage> ReadEntriesAsync(MessageAddress conversation, Guid taskId, Guid agentId, long afterSequence, int pageSize, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        if (afterSequence < 0 || pageSize is < 1 or > 100) throw new ArgumentException("Agent 历史游标或页大小无效。");
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database =>
        {
            List<AgentEntryEntity> entries = database.AgentEntries.AsNoTracking().Where(entry => entry.TaskId == taskId && entry.AgentId == agentId && entry.Sequence > afterSequence &&
                database.AgentTasks.Any(task => task.Id == taskId && task.ConversationKey == key)).OrderBy(entry => entry.Sequence).Take(pageSize + 1).ToList();
            bool more = entries.Count > pageSize;
            if (more) entries.RemoveAt(entries.Count - 1);
            return new AgentEntryPage { Entries = entries.Select(AgentEntrySnapshotOf).ToImmutableArray(), NextSequence = more ? entries[^1].Sequence : null };
        }, cancellationToken);
    }

    /// <summary>记录正文先冻结为 JSON 文本，写执行器不持有外部 JsonDocument。</summary>
    public Task<AgentEntry> AppendEntryAsync(Guid agentId, AgentEntryKind kind, JsonElement data, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind) || data.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Agent 记录种类或内容无效。");
        string json = data.GetRawText();
        return WriteAsync(database =>
        {
            AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == agentId);
            AgentEntryEntity entry = AddAgentEntry(database, agent, kind, json, now);
            database.SaveChanges();
            return AgentEntrySnapshotOf(entry);
        }, cancellationToken);
    }

    /// <summary>成果引用与接收者序号一起提交，重复投递返回同一个审计身份。</summary>
    public Task<AgentEntry> AppendResultNotificationAsync(Guid agentId, Guid resultAgentId, long revision, DateTimeOffset now, CancellationToken cancellationToken)
        => WriteAsync(database =>
        {
            AgentSessionEntity recipient = database.AgentSessions.Single(agent => agent.Id == agentId);
            AgentSessionEntity source = database.AgentSessions.Single(agent => agent.Id == resultAgentId);
            if (recipient.TaskId != source.TaskId || revision < 1 || source.ResultRevision < revision) throw new ArgumentException("成果通知必须引用同树已保存的修订。");
            AgentEntryEntity? existing = database.AgentEntries.SingleOrDefault(entry => entry.AgentId == agentId && entry.SourceResultAgentId == resultAgentId && entry.SourceResultRevision == revision);
            if (existing is not null) return AgentEntrySnapshotOf(existing);
            AgentEntryEntity notification = AddAgentEntry(database, recipient, AgentEntryKind.AgentMessage, JsonSerializer.Serialize(new Dictionary<string, object>
            { ["kind"] = "result_notification", ["agent_id"] = resultAgentId, ["revision"] = revision }), now);
            notification.SourceResultAgentId = resultAgentId; notification.SourceResultRevision = revision;
            database.SaveChanges(); return AgentEntrySnapshotOf(notification);
        }, cancellationToken);

    /// <summary>关闭代理不能重新进入活动状态，停止中的实际执行诊断仍可保存。</summary>
    public Task SetActivityAsync(Guid agentId, AgentActivity activity, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        if (!Enum.IsDefined(activity)) throw new ArgumentException("代理活动状态无效。");
        AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == agentId);
        if (agent.ExitedAt.HasValue && activity != AgentActivity.Closed) throw new InvalidOperationException("已退出的代理不能重新运行。");
        agent.Activity = activity;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>成果和修订在同一提交内可见，调用方随后才能投递完成通知。</summary>
    public Task<AgentSnapshot> CompleteTurnAsync(Guid agentId, AgentResult result, DateTimeOffset now, bool closed, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result);
        return WriteAsync(database =>
        {
            AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == agentId);
            agent.ResultJson = json;
            agent.ResultRevision++;
            agent.Activity = closed ? AgentActivity.Closed : AgentActivity.Idle;
            if (closed) agent.ExitedAt = now.ToUnixTimeMilliseconds();
            AddAgentEntry(database, agent, AgentEntryKind.Result, json, now);
            database.SaveChanges(); return AgentSnapshotOf(agent);
        }, cancellationToken);
    }

    /// <summary>任务预算与单代理决策次数原子预留，停止整理最多一次且必须明确标识。</summary>
    public Task<bool> ReserveCallsAsync(Guid agentId, int modelCalls, int toolCalls, bool decision, bool settling, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        if (modelCalls < 0 || toolCalls < 0 || modelCalls == 0 && toolCalls == 0 || decision && modelCalls != 1 || settling && (decision || modelCalls != 1 || toolCalls != 0))
            throw new ArgumentException("Agent 调用预算请求无效。");
        AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == agentId);
        AgentTaskEntity task = database.AgentTasks.Single(item => item.Id == agent.TaskId);
        AgentBudget budget = JsonSerializer.Deserialize<AgentProfile>(task.InitialProfileJson)!.Budget;
        if (agent.ExitedAt.HasValue || task.ReleasedAt.HasValue || task.State is not (AgentTaskState.Active or AgentTaskState.Stopping)) return false;
        if (settling ? !agent.MaySettle || agent.SettlementUsed || !agent.StopRequestedAt.HasValue : task.State != AgentTaskState.Active || agent.StopRequestedAt.HasValue) return false;
        if (modelCalls > budget.ModelCalls - task.ModelCalls || toolCalls > budget.ToolCalls - task.ToolCalls || decision && agent.DecisionCalls >= budget.DecisionsPerAgent) return false;
        task.ModelCalls += modelCalls; task.ToolCalls += toolCalls;
        if (decision) agent.DecisionCalls++;
        if (settling) agent.SettlementUsed = true;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>子代理累计数和深度共同限制创建，已关闭子代理不会返还累计额度。</summary>
    public Task<AgentSnapshot?> CreateChildAsync(Guid parentId, string templateId, string task, JsonElement input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        string json = input.GetRawText();
        return WriteAsync(database =>
        {
            AgentSessionEntity parent = database.AgentSessions.Single(item => item.Id == parentId);
            AgentTaskEntity root = database.AgentTasks.Single(item => item.Id == parent.TaskId);
            AgentProfile profile = JsonSerializer.Deserialize<AgentProfile>(root.InitialProfileJson)!;
            if (!profile.Templates.Any(template => template.Id == templateId)) throw new ArgumentException("子代理模板不存在。");
            if (root.State != AgentTaskState.Active || parent.StopRequestedAt.HasValue || parent.ExitedAt.HasValue ||
                root.TotalAgents >= profile.Budget.TotalAgents || parent.Depth >= profile.Budget.MaximumDepth) return null;
            AgentSessionEntity child = new()
            {
                Id = Guid.NewGuid(),
                TaskId = root.Id,
                ParentId = parent.Id,
                Depth = parent.Depth + 1,
                TemplateId = templateId,
                Assignment = task,
                CreatedAt = now.ToUnixTimeMilliseconds(),
                Activity = AgentActivity.Ready
            };
            database.AgentSessions.Add(child);
            root.TotalAgents++;
            AddAgentEntry(database, child, AgentEntryKind.Followup, json, now);
            database.SaveChanges(); return AgentSnapshotOf(child);
        }, cancellationToken);
    }

    /// <summary>整棵目标子树共用截止时间，重复停止不延长期限或增加整理机会。</summary>
    public Task RequestStopAsync(Guid taskId, Guid targetAgentId, ImmutableArray<Guid> agents, DateTimeOffset now, DateTimeOffset deadline, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        if (agents.IsDefaultOrEmpty || !agents.Contains(targetAgentId) || deadline < now) throw new ArgumentException("停止目标或期限无效。");
        AgentTaskEntity task = database.AgentTasks.Single(item => item.Id == taskId);
        Guid[] selected = agents.ToArray();
        List<AgentSessionEntity> rows = database.AgentSessions.Where(agent => agent.TaskId == taskId && selected.Contains(agent.Id)).ToList();
        if (rows.Count != agents.Distinct().Count()) throw new ArgumentException("停止目标不属于当前任务。");
        foreach (AgentSessionEntity agent in rows)
        {
            if (agent.ExitedAt.HasValue) continue;
            bool firstStop = !agent.StopRequestedAt.HasValue;
            agent.StopRequestedAt ??= now.ToUnixTimeMilliseconds();
            agent.StopDeadline = agent.StopDeadline.HasValue ? Math.Min(agent.StopDeadline.Value, deadline.ToUnixTimeMilliseconds()) : deadline.ToUnixTimeMilliseconds();
            if (firstStop) agent.MaySettle = agent.Id == targetAgentId;
            else if (agent.Id != targetAgentId) agent.MaySettle = false;
            agent.Activity = AgentActivity.Stopping;
        }
        if (task.RootAgentId == targetAgentId && task.State == AgentTaskState.Active) task.State = AgentTaskState.Stopping;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>调用方证明工作退出后才释放归属，结束状态本身不会自动释放。</summary>
    public Task FinishTaskAsync(Guid taskId, AgentTaskState state, string? error, DateTimeOffset now, bool release, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        if (state is AgentTaskState.Active or AgentTaskState.Stopping || !Enum.IsDefined(state)) throw new ArgumentException("任务结束必须采用终态。");
        AgentTaskEntity task = database.AgentTasks.Single(item => item.Id == taskId);
        task.State = state; task.Error = error; task.FinishedAt ??= now.ToUnixTimeMilliseconds();
        if (release)
        {
            task.ReleasedAt ??= now.ToUnixTimeMilliseconds();
            foreach (AgentSessionEntity agent in database.AgentSessions.Where(item => item.TaskId == taskId))
            { agent.Activity = AgentActivity.Closed; agent.ExitedAt ??= now.ToUnixTimeMilliseconds(); }
        }
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>在当前写入事务中分配单调序号，不独立提交半条任务记录。</summary>
    private static AgentEntryEntity AddAgentEntry(MissiliDbContext database, AgentSessionEntity agent, AgentEntryKind kind, string json, DateTimeOffset now)
    {
        AgentEntryEntity entry = new()
        { Id = Guid.NewGuid(), TaskId = agent.TaskId, AgentId = agent.Id, Sequence = ++agent.NextSequence, Kind = kind, CreatedAt = now.ToUnixTimeMilliseconds(), DataJson = json };
        database.AgentEntries.Add(entry); return entry;
    }

    /// <summary>所有实体转换为无跟踪共享快照，公开时间恢复为 UTC 偏移类型。</summary>
    private static AgentTaskSnapshot AgentTaskSnapshotOf(AgentTaskEntity task) => new()
    {
        Id = task.Id,
        RootAgentId = task.RootAgentId,
        RunId = task.RunId,
        Conversation = JsonSerializer.Deserialize<MessageAddress>(task.ConversationKey)!,
        TriggerSenderId = task.TriggerSenderId,
        ProfileId = task.ProfileId,
        ConfigurationRevision = task.ConfigurationRevision,
        InitialProfile = JsonSerializer.Deserialize<AgentProfile>(task.InitialProfileJson)!,
        State = task.State,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(task.CreatedAt),
        FinishedAt = AgentTime(task.FinishedAt),
        ReleasedAt = AgentTime(task.ReleasedAt),
        Error = task.Error,
        Usage = new AgentUsage { ModelCalls = task.ModelCalls, ToolCalls = task.ToolCalls, TotalAgents = task.TotalAgents }
    };

    /// <summary>成果 JSON 只恢复宿主类型，不创建插件实例或执行历史动作。</summary>
    private static AgentSnapshot AgentSnapshotOf(AgentSessionEntity agent) => new()
    {
        Id = agent.Id,
        TaskId = agent.TaskId,
        ParentId = agent.ParentId,
        Depth = agent.Depth,
        TemplateId = agent.TemplateId,
        Assignment = agent.Assignment,
        Activity = agent.Activity,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(agent.CreatedAt),
        StopRequestedAt = AgentTime(agent.StopRequestedAt),
        StopDeadline = AgentTime(agent.StopDeadline),
        ExitedAt = AgentTime(agent.ExitedAt),
        DecisionCalls = agent.DecisionCalls,
        ResultRevision = agent.ResultRevision,
        Result = agent.ResultJson is null ? null : JsonSerializer.Deserialize<AgentResult>(agent.ResultJson)
    };

    /// <summary>共享 JSON 值复制到当前读取结果，不关联数据库或文档句柄。</summary>
    private static AgentEntry AgentEntrySnapshotOf(AgentEntryEntity entry) => new()
    {
        Id = entry.Id,
        TaskId = entry.TaskId,
        AgentId = entry.AgentId,
        Sequence = entry.Sequence,
        Kind = entry.Kind,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAt),
        SourceMessageId = entry.SourceMessageId,
        Data = ParseJson(entry.DataJson)!.Value
    };

    /// <summary>空时间保持空值，不把未结束的任务伪造为已有完成时间。</summary>
    private static DateTimeOffset? AgentTime(long? value) => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;
}
