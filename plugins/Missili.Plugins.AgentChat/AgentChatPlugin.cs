using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Plugins.AgentChat;

/// <summary>官方入口只登记消息唤醒和固定管理命令，是否回应或开展工作由主代理决定。</summary>
public sealed class AgentChatPlugin : IPlugin, IPluginConfigurationValidator
{
    private const string PluginId = "missili.plugins.agent-chat";
    private const string Usage = "管理命令：/agent status、/agent result <taskId>、/agent stop [agentId]、/agent cancel。";
    private IPluginContext? _context;

    /// <summary>管理路径独立登记，其他消息统一经过一次唤醒判断；加载时不连接模型。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _context = context;
        context.ConfigurationValidators.Register(this);
        foreach (string command in new[] { "status", "result", "stop", "cancel" })
            context.Handlers.RegisterCommand(command, "Agent 运行管理", "查询或控制当前会话运行", ["/agent", command], 0, CommandAsync, AllowedManagement);
        context.Handlers.RegisterListener(MessageHooks.Received, "wake", "主代理唤醒", "按前缀、提及、关键词或概率安排一次决策", 0, WakeAsync);
        return Task.CompletedTask;
    }

    /// <summary>入口没有自己拥有的代理工作，停止入口不撤销宿主已登记的运行。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>私有配置严格解析为共享错误，不将插件 DTO 或异常交给宿主长期持有。</summary>
    public Task<ImmutableArray<PluginConfigurationError>> ValidateAsync(PluginConfigurationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = WakeSettings.Read(context.Configuration); return Task.FromResult(ImmutableArray<PluginConfigurationError>.Empty); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { return Task.FromResult(ImmutableArray.Create(new PluginConfigurationError { Path = "$", Message = exception.Message })); }
    }

    /// <summary>一次捕获规则并按顺序短路；没有正文也允许唤醒，失败只记录诊断而不自动发言。</summary>
    private async Task WakeAsync(MessageContext message, CancellationToken token)
    {
        if (!Incoming(message)) return;
        ConversationProfileSnapshot? profile = _context!.Profiles.TryResolve(message.OriginalMessage.Address);
        if (profile?.Profile.Agent.Enabled != true) return;
        WakeSettings settings = Settings(profile);
        if (!settings.Allows(message.OriginalMessage.Address)) return;
        ImmutableArray<MessageTextFragment> fragments = MessageText.Fragments(message.CurrentContent);
        AgentWakeCause? cause = null;
        ImmutableArray<MessageSegment> content = message.CurrentContent;
        MessageTextFragment? first = fragments.FirstOrDefault();
        if (first is not null)
        {
            int start = first.Text.Length - first.Text.TrimStart().Length;
            string? prefix = null;
            foreach (string candidate in settings.WakePrefixes)
                if (first.Text.AsSpan(start).StartsWith(candidate, StringComparison.Ordinal) && (prefix is null || candidate.Length > prefix.Length)) prefix = candidate;
            if (prefix is not null)
            {
                cause = new AgentWakeCause { Kind = AgentWakeKind.Prefix, ConfigurationRevision = profile.Revision, MatchedText = prefix };
                content = MessageText.Remove(content, first, start, prefix.Length);
            }
        }
        if (cause is null && settings.Mentions && content.Any(segment => segment.Kind == SegmentTypes.Mention &&
            segment.ParseState == SegmentParseState.Parsed && segment.MentionTarget() == message.OriginalMessage.Address.BotAccountId))
            cause = new AgentWakeCause { Kind = AgentWakeKind.Mention, ConfigurationRevision = profile.Revision };
        if (cause is null)
        {
            string? keyword = settings.Keywords.FirstOrDefault(word => fragments.Any(fragment => fragment.Text.Contains(word, StringComparison.OrdinalIgnoreCase)));
            if (keyword is not null) cause = new AgentWakeCause { Kind = AgentWakeKind.Keyword, ConfigurationRevision = profile.Revision, MatchedText = keyword };
        }
        if (cause is null && settings.ProbabilityEnabled && settings.Probability > 0)
        {
            double sample = Random.Shared.NextDouble();
            if (sample < settings.Probability)
                cause = new AgentWakeCause { Kind = AgentWakeKind.Probability, ConfigurationRevision = profile.Revision, Probability = settings.Probability, Sample = sample };
        }
        if (cause is null) return;
        AgentWakeResult result = await _context.Agents.WakeAsync(new AgentWakeRequest { MessageId = message.OriginalMessage.Id, CurrentContent = content, Cause = cause }, token).ConfigureAwait(false);
        if (!result.Registered) _context.Logger.LogWarning("消息 {MessageId} 未登记主代理唤醒：{State}", message.OriginalMessage.Id, result.State);
    }

    /// <summary>命令参数使用宿主匹配结果；拒绝或用法错误仍完成命令接管，不进入概率唤醒。</summary>
    private async Task CommandAsync(MessageContext message, CancellationToken token)
    {
        CommandMatch command = message.Command!;
        string arguments = string.Join(" ", MessageText.Fragments(command.Arguments).Select(fragment => fragment.Text)).Trim();
        switch (command.Path[1])
        {
            case "status":
                if (arguments.Length != 0) { await StatusAsync(message, Usage, token).ConfigureAwait(false); return; }
                AgentTaskStatus? status = await _context!.Agents.GetActiveAsync(message.OriginalMessage.Address, token).ConfigureAwait(false);
                string state = status is null ? "当前会话没有活动运行。" : $"运行 {status.Task.Id}\n状态：{StateName(status.Task.State)}；实际工作：{status.ActiveWork}；等待代理：{status.WaitingAgents}；待处理消息：{status.PendingInputs}\n模型调用：{status.Task.Usage.ModelCalls}/{status.Task.InitialProfile.Budget.ModelCalls}；工具调用：{status.Task.Usage.ToolCalls}/{status.Task.InitialProfile.Budget.ToolCalls}；代理数：{status.Task.Usage.TotalAgents}/{status.Task.InitialProfile.Budget.TotalAgents}";
                await StatusAsync(message, state, token).ConfigureAwait(false); return;
            case "result":
                if (!Guid.TryParse(arguments, out Guid task)) { await StatusAsync(message, Usage, token).ConfigureAwait(false); return; }
                await ResultAsync(message, task, token).ConfigureAwait(false); return;
            case "stop":
            case "cancel":
                Guid? target = null;
                if (arguments.Length > 0)
                {
                    if (command.Path[1] == "cancel" || !Guid.TryParse(arguments, out Guid parsed)) { await StatusAsync(message, Usage, token).ConfigureAwait(false); return; }
                    target = parsed;
                }
                AgentControlResult control = await _context!.Agents.RequestStopAsync(new AgentStopRequest { MessageId = message.OriginalMessage.Id, AgentId = target, Emergency = command.Path[1] == "cancel" }, token).ConfigureAwait(false);
                await StatusAsync(message, control.Accepted ? "已请求停止当前运行；实际退出状态可用 /agent status 查询。后续新消息仍按唤醒配置处理。" : control.Error!, token).ConfigureAwait(false); return;
            default: throw new InvalidOperationException("未登记的管理命令。");
        }
    }

    /// <summary>运行记录仅在当前会话读取，结束状态不宣称用户目标已经完成。</summary>
    private async Task ResultAsync(MessageContext source, Guid id, CancellationToken token)
    {
        MessageAddress address = source.OriginalMessage.Address;
        AgentTaskSnapshot? task = await _context!.AgentHistory.GetTaskAsync(address, id, token).ConfigureAwait(false);
        if (task is null) { await StatusAsync(source, "当前会话没有该运行记录。", token).ConfigureAwait(false); return; }
        ImmutableArray<AgentSnapshot> agents = await _context.AgentHistory.GetAgentsAsync(address, id, token).ConfigureAwait(false);
        AgentSnapshot root = agents.Single(agent => agent.Id == task.RootAgentId);
        StringBuilder text = new($"运行 {task.Id}，状态：{StateName(task.State)}\n");
        if (root.Result is AgentResult result)
        {
            text.AppendLine(result.Complete ? "运行结果：" : "部分运行结果：");
            string body = string.Concat(result.Content.Where(block => block.Kind == ModelBlockKind.Text).Select(block => block.Text));
            if (body.Length > 8000) { int end = char.IsHighSurrogate(body[7999]) ? 7999 : 8000; text.Append(body.AsSpan(0, end)).AppendLine("\n[正文较长，此处为预览；完整内容保存在运行记录中。]"); }
            else text.AppendLine(body);
            if (result.Unfinished is not null) text.AppendLine(result.Unfinished);
            if (result.Error is not null) text.AppendLine(result.Error);
        }
        ImmutableArray<BatchSendResult> batches = await _context.OutgoingHistory.GetTaskBatchesAsync(address, id, 20, token).ConfigureAwait(false);
        if (!batches.IsEmpty) text.AppendLine("最近发送批次：");
        foreach (BatchSendResult batch in batches) text.AppendLine($"{batch.BatchId}：{batch.State}，已尝试 {batch.Messages.Length}/{batch.TotalMessages}，记录 {(batch.Recorded ? "成功" : "失败")}");
        await StatusAsync(source, text.ToString(), token).ConfigureAwait(false);
    }

    /// <summary>管理入口不依赖 Agent 启用或任一唤醒开关，实际停止权限由宿主核验。</summary>
    private static bool AllowedManagement(MessageContext message) => Incoming(message);

    /// <summary>自身发送及自身账号的重复上报不能唤醒自己或执行管理命令。</summary>
    private static bool Incoming(MessageContext message) => message.OriginalMessage.Direction == MessageDirection.Incoming && message.OriginalMessage.SenderId != message.OriginalMessage.Address.BotAccountId;

    /// <summary>缺省配置只使用入口自己的规则，不将配置档关闭解释为关闭管理能力。</summary>
    private static WakeSettings Settings(ConversationProfileSnapshot? profile) => profile is not null && profile.Profile.Plugins.TryGetValue(PluginId, out JsonElement json) ? WakeSettings.Read(json) : new WakeSettings();

    /// <summary>固定回执继续通过统一出站路径，用户配置的处理与审批仍然生效。</summary>
    private async Task StatusAsync(MessageContext source, string text, CancellationToken token)
        => await _context!.Sender.SendAsync(OutgoingBatch.Single(source.OriginalMessage.Address, [SegmentValues.Text(text)], "status") with { TriggerMessageId = source.OriginalMessage.Id }, token).ConfigureAwait(false);

    /// <summary>公开状态描述运行生命周期，不把正常结束包装成完成了用户请求。</summary>
    private static string StateName(AgentTaskState state) => state switch
    {
        AgentTaskState.Active => "运行中", AgentTaskState.Stopping => "停止中", AgentTaskState.Completed => "已结束",
        AgentTaskState.Cancelled => "已取消", AgentTaskState.Failed => "失败", AgentTaskState.Interrupted => "中断",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
