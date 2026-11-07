using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>动作权限结果保留停止与权限拒绝的区别。</summary>
public enum AgentActionAccess { Allowed, Stopped, Denied }

/// <summary>工具与出站共用任务授权边界，新增配置不能扩大旧任务的权限上限。</summary>
public sealed class AgentAuthorization
{
    private readonly IAgentHistory _agents;
    private readonly IMessageHistory _history;
    private readonly IConversationProfileService _profiles;

    /// <summary>权限和证据从宿主持久化来源读取，不接受模型自报身份。</summary>
    public AgentAuthorization(IAgentHistory agents, IMessageHistory history, IConversationProfileService profiles)
    { _agents = agents; _history = history; _profiles = profiles; }

    /// <summary>固定任务上限与当前配置取交集；空工具只允许根代理的宿主直接终答。</summary>
    public async Task<AgentActionAccess> CheckAsync(MessageAddress conversation, OperationOrigin origin, string? toolId, ImmutableArray<string> permissions, CancellationToken token)
    {
        if (origin.TaskId is not Guid taskId || origin.AgentId is not Guid agentId) return AgentActionAccess.Denied;
        AgentTaskSnapshot? task = await _agents.GetTaskAsync(conversation, taskId, token).ConfigureAwait(false);
        if (task is null) return AgentActionAccess.Denied;
        if (task.State != AgentTaskState.Active || task.ReleasedAt.HasValue) return AgentActionAccess.Stopped;
        AgentSnapshot? agent = (await _agents.GetAgentsAsync(conversation, taskId, token).ConfigureAwait(false)).SingleOrDefault(item => item.Id == agentId);
        ConversationProfileSnapshot? current = _profiles.TryResolve(conversation);
        if (agent is null || current?.Profile.Agent.Enabled != true) return AgentActionAccess.Denied;
        if (agent.ExitedAt.HasValue || agent.StopRequestedAt.HasValue) return AgentActionAccess.Stopped;
        if (toolId is null) return agent.Id == task.RootAgentId ? AgentActionAccess.Allowed : AgentActionAccess.Denied;
        bool allowed;
        if (agent.ParentId is null) allowed = task.InitialProfile.Tools.Contains(toolId, StringComparer.Ordinal) && current.Profile.Agent.Tools.Contains(toolId, StringComparer.Ordinal);
        else
        {
            SubagentTemplate? initial = task.InitialProfile.Templates.SingleOrDefault(item => item.Id == agent.TemplateId);
            SubagentTemplate? active = current.Profile.Agent.Templates.SingleOrDefault(item => item.Id == agent.TemplateId);
            allowed = initial is not null && active is not null && initial.Tools.Contains(toolId, StringComparer.Ordinal) && active.Tools.Contains(toolId, StringComparer.Ordinal) &&
                task.InitialProfile.DelegatedTools.Contains(toolId, StringComparer.Ordinal) && current.Profile.Agent.DelegatedTools.Contains(toolId, StringComparer.Ordinal);
        }
        if (permissions.IsDefault) return AgentActionAccess.Denied;
        if (agent.ParentId is null) allowed &= permissions.All(permission => task.InitialProfile.Permissions.Contains(permission, StringComparer.Ordinal) && current.Profile.Agent.Permissions.Contains(permission, StringComparer.Ordinal));
        else
        {
            SubagentTemplate? initial = task.InitialProfile.Templates.SingleOrDefault(item => item.Id == agent.TemplateId);
            SubagentTemplate? active = current.Profile.Agent.Templates.SingleOrDefault(item => item.Id == agent.TemplateId);
            allowed &= initial is not null && active is not null && permissions.All(permission => initial.Permissions.Contains(permission, StringComparer.Ordinal) && active.Permissions.Contains(permission, StringComparer.Ordinal) &&
                task.InitialProfile.DelegatedPermissions.Contains(permission, StringComparer.Ordinal) && current.Profile.Agent.DelegatedPermissions.Contains(permission, StringComparer.Ordinal));
        }
        return allowed ? AgentActionAccess.Allowed : AgentActionAccess.Denied;
    }

    /// <summary>真实要求与发言者来源独立于代理说明；撤回占位不会泄露旧正文。</summary>
    public async Task<ImmutableArray<ToolApprovalEvidence>> EvidenceAsync(MessageAddress conversation, OperationOrigin origin,
        long? triggerMessageId, ConversationProfileSnapshot profile, CancellationToken token)
    {
        HashSet<long> messages = [];
        Dictionary<long, AgentWakeCause> causes = [];
        ImmutableArray<ToolApprovalEvidence>.Builder evidence = ImmutableArray.CreateBuilder<ToolApprovalEvidence>();
        if (triggerMessageId.HasValue) messages.Add(triggerMessageId.Value);
        AgentTaskSnapshot? task = origin.TaskId is Guid taskId ? await _agents.GetTaskAsync(conversation, taskId, token).ConfigureAwait(false) : null;
        if (task is not null)
        {
            long after = 0;
            while (true)
            {
                AgentEntryPage page = await _agents.ReadEntriesAsync(conversation, task.Id, task.RootAgentId, after, 100, token).ConfigureAwait(false);
                foreach (AgentEntry entry in page.Entries)
                    if (entry.Kind == AgentEntryKind.WakeInput && entry.SourceMessageId is long id)
                    { messages.Add(id); causes[id] = JsonSerializer.Deserialize<AgentWakeInput>(entry.Data)!.Cause; }
                if (page.NextSequence is not long next) break;
                after = next;
            }
            ImmutableArray<AgentSnapshot> agents = await _agents.GetAgentsAsync(conversation, task.Id, token).ConfigureAwait(false);
            AgentSnapshot? agent = agents.SingleOrDefault(item => item.Id == origin.AgentId);
            while (agent is not null)
            {
                evidence.Add(new ToolApprovalEvidence
                {
                    Kind = "agent_assignment",
                    AgentId = agent.Id,
                    Content = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["parent_agent_id"] = agent.ParentId, ["assignment"] = agent.ParentId.HasValue ? agent.Assignment : null, ["template"] = agent.TemplateId })
                });
                agent = agent.ParentId.HasValue ? agents.SingleOrDefault(item => item.Id == agent.ParentId) : null;
            }
        }
        foreach (long id in messages.Order())
        {
            MessageSnapshot? message = await _history.GetAsync(id, token).ConfigureAwait(false);
            if (message is null || message.Address != conversation || message.Direction != MessageDirection.Incoming) continue;
            bool hidden = (message.Recalled || message.Deleted) && profile.Profile.Visibility == MessageVisibilityPolicy.Placeholder;
            bool administrator = profile.Profile.Agent.Administrators.Contains(message.SenderId, StringComparer.Ordinal);
            AgentWakeCause? cause = causes.GetValueOrDefault(id);
            JsonElement content = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["segments"] = hidden ? null : message.Segments,
                ["hidden"] = hidden,
                ["recalled_or_deleted"] = profile.Profile.Visibility != MessageVisibilityPolicy.Original && (message.Recalled || message.Deleted),
                ["wake_reason"] = hidden && cause is not null ? cause with { MatchedText = null } : cause,
                ["configured_administrator"] = administrator
            });
            evidence.Add(new ToolApprovalEvidence { Kind = "user_message", SenderId = message.SenderId, MessageId = message.Id, Content = content });
        }
        return evidence.ToImmutable();
    }
}
