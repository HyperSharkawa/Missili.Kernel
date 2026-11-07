using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>工具写入复用单写执行器，预算与整组意图在同一次提交中生效。</summary>
public sealed partial class SqliteStore : IAgentToolStore
{
    /// <summary>已有完整模型条目是执行前提，已存在的组只返回记录而不授予再次执行资格。</summary>
    public Task<AgentToolReservation> BeginToolGroupAsync(AgentToolGroup group, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (group.Calls.IsDefaultOrEmpty || group.Model.State != ModelCallState.Succeeded || !group.Model.Complete ||
            group.Calls.Select(call => call.ToolCallId).Distinct(StringComparer.Ordinal).Count() != group.Calls.Length)
            throw new ArgumentException("工具组必须来自完整模型输出且调用身份唯一。");
        string model = JsonSerializer.Serialize(group.Model);
        string[] intents = group.Calls.Select(call => JsonSerializer.Serialize(call)).ToArray();
        return WriteAsync(database =>
        {
            List<AgentToolCallEntity> existing = database.AgentToolCalls.Where(call => call.AgentId == group.AgentId && call.ModelCallId == group.Model.CallId).OrderBy(call => call.Position).ToList();
            if (existing.Count > 0)
            {
                if (!existing.Select(call => call.ToolCallId).SequenceEqual(group.Calls.Select(call => call.ToolCallId))) throw new InvalidOperationException("工具组重入不能更改原调用身份。");
                return new AgentToolReservation { Calls = existing.Select(ToolSnapshot).ToImmutableArray() };
            }
            ModelCallEntity savedModel = database.ModelCalls.Single(call => call.Id == group.Model.CallId);
            AgentEntryEntity? entry = database.AgentEntries.SingleOrDefault(item => item.Id == group.ModelEntryId);
            if (entry is null || entry.AgentId != group.AgentId || entry.Kind != AgentEntryKind.ModelOutput || entry.DataJson != model)
                throw new InvalidOperationException("工具组缺少匹配的完整模型输出条目。");
            AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == group.AgentId);
            AgentTaskEntity task = database.AgentTasks.Single(item => item.Id == agent.TaskId);
            if (savedModel.State != ModelCallState.Succeeded || savedModel.AgentId != agent.Id || savedModel.TaskId != task.Id || savedModel.TurnId != group.TurnId)
                throw new InvalidOperationException("工具请求没有对应的完整模型记录与调用归属。");
            AgentBudget budget = JsonSerializer.Deserialize<AgentProfile>(task.InitialProfileJson)!.Budget;
            if (task.State != AgentTaskState.Active || task.ReleasedAt.HasValue || agent.StopRequestedAt.HasValue || agent.ExitedAt.HasValue || group.Calls.Length > budget.ToolCalls - task.ToolCalls)
                return new AgentToolReservation { Error = "任务已停止或没有足够的整组工具预算。" };
            List<AgentToolCallEntity> rows = [];
            for (int index = 0; index < group.Calls.Length; index++)
                rows.Add(new AgentToolCallEntity
                {
                    Id = group.Calls[index].Id,
                    TaskId = task.Id,
                    AgentId = agent.Id,
                    TurnId = group.TurnId,
                    ModelCallId = group.Model.CallId,
                    ToolCallId = group.Calls[index].ToolCallId,
                    Position = index,
                    PluginId = group.Calls[index].Tool?.PluginId,
                    IntentJson = intents[index],
                    State = ToolCallState.Pending,
                    CreatedAt = now.ToUnixTimeMilliseconds()
                });
            task.ToolCalls += rows.Count;
            database.AgentToolCalls.AddRange(rows);
            database.SaveChanges();
            return new AgentToolReservation { Created = true, Calls = rows.Select(ToolSnapshot).ToImmutableArray() };
        }, cancellationToken);
    }

    /// <summary>准备结果一次冻结，不能在批准后再次改写实际参数。</summary>
    public Task PrepareToolAsync(Guid id, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(prepared);
        return WriteAsync(database =>
        {
            AgentToolCallEntity row = database.AgentToolCalls.Single(call => call.Id == id);
            if (row.State != ToolCallState.Pending) throw new InvalidOperationException("调用已经离开准备阶段。");
            row.PreparedJson = json; row.State = ToolCallState.Reviewing; database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>保存绑定实际动作的批准，不允许重入覆盖原结论。</summary>
    public Task ApproveToolAsync(Guid id, ToolApprovalRequest request, ToolApprovalResult approval, CancellationToken cancellationToken)
    {
        string input = JsonSerializer.Serialize(request), output = JsonSerializer.Serialize(approval);
        return WriteAsync(database =>
        {
            AgentToolCallEntity row = database.AgentToolCalls.Single(call => call.Id == id);
            if (row.State != ToolCallState.Reviewing || row.ApprovalJson is not null) throw new InvalidOperationException("调用不允许再次写入审批。");
            row.ApprovalRequestJson = input; row.ApprovalJson = output; database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>任何写入故障或停止标志都发生在实际执行之前。</summary>
    public Task<bool> StartToolAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        AgentToolCallEntity row = database.AgentToolCalls.Single(call => call.Id == id);
        AgentSessionEntity agent = database.AgentSessions.Single(item => item.Id == row.AgentId);
        AgentTaskEntity task = database.AgentTasks.Single(item => item.Id == row.TaskId);
        if (row.State != ToolCallState.Reviewing || row.StartedAt.HasValue || row.ApprovalJson is null ||
            !JsonSerializer.Deserialize<ToolApprovalResult>(row.ApprovalJson)!.Permitted || task.State != AgentTaskState.Active || agent.StopRequestedAt.HasValue || agent.ExitedAt.HasValue) return false;
        row.StartedAt = now.ToUnixTimeMilliseconds(); row.State = ToolCallState.Running;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>先记录真实插件结果再启动出站处理，不将生成完成视为消息已发送。</summary>
    public Task SaveToolExecutionAsync(Guid id, ToolExecutionResult result, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result);
        return WriteAsync(database =>
        {
            AgentToolCallEntity row = database.AgentToolCalls.Single(call => call.Id == id);
            if (row.State != ToolCallState.Running) throw new InvalidOperationException("调用并未开始实际执行。");
            row.ExecutionJson = json; row.State = ToolCallState.Completing; database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>终局只写一次，保存失败由调用者保留内存成果而不重新执行。</summary>
    public Task FinishToolAsync(AgentToolResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result);
        return WriteAsync(database =>
        {
            AgentToolCallEntity row = database.AgentToolCalls.Single(call => call.Id == result.InvocationId);
            if (row.FinishedAt.HasValue) throw new InvalidOperationException("工具已经形成最终记录。");
            row.ResultJson = json; row.State = result.State; row.FinishedAt = now.ToUnixTimeMilliseconds(); database.SaveChanges(); return true;
        }, cancellationToken);
    }

    /// <summary>完整会话检查先于内容返回，未知或越界身份统一返回空值。</summary>
    public Task<AgentToolRecord?> GetToolCallAsync(MessageAddress conversation, Guid id, CancellationToken cancellationToken = default)
    {
        conversation.Validate();
        string key = JsonSerializer.Serialize(conversation);
        return ReadAsync(database =>
        {
            AgentToolCallEntity? row = database.AgentToolCalls.AsNoTracking().SingleOrDefault(call => call.Id == id && database.AgentTasks.Any(task => task.Id == call.TaskId && task.ConversationKey == key));
            return row is null ? null : ToolSnapshot(row);
        }, cancellationToken);
    }

    /// <summary>记录只反序列化为共享类型，不恢复调用执行器。</summary>
    private static AgentToolRecord ToolSnapshot(AgentToolCallEntity row) => new()
    {
        Intent = JsonSerializer.Deserialize<AgentToolIntent>(row.IntentJson)!,
        ModelCallId = row.ModelCallId,
        Position = row.Position,
        State = row.State,
        Origin = new OperationOrigin { TaskId = row.TaskId, AgentId = row.AgentId, TurnId = row.TurnId, ToolInvocationId = row.Id, PluginId = row.PluginId },
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt),
        StartedAt = AgentTime(row.StartedAt),
        FinishedAt = AgentTime(row.FinishedAt),
        Prepared = row.PreparedJson is null ? null : JsonSerializer.Deserialize<PreparedToolCall>(row.PreparedJson),
        ApprovalRequest = row.ApprovalRequestJson is null ? null : JsonSerializer.Deserialize<ToolApprovalRequest>(row.ApprovalRequestJson),
        Approval = row.ApprovalJson is null ? null : JsonSerializer.Deserialize<ToolApprovalResult>(row.ApprovalJson),
        Execution = row.ExecutionJson is null ? null : JsonSerializer.Deserialize<ToolExecutionResult>(row.ExecutionJson),
        Result = row.ResultJson is null ? null : JsonSerializer.Deserialize<AgentToolResult>(row.ResultJson)
    };
}
