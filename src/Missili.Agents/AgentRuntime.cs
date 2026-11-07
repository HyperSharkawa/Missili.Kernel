using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>任务树、邮箱与每代理推进资格的唯一所有者；登记唤醒后工作完全属于宿主。</summary>
public sealed partial class AgentRuntime : IAgentService, IAgentToolHandler, IAsyncDisposable
{
    private readonly SemaphoreSlim _coordination = new(1, 1);
    private readonly object _capacityGate = new();
    private readonly Dictionary<MessageAddress, Tree> _trees = [];
    private readonly HashSet<Task> _runs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly IAgentStore _store;
    private readonly IModelStore _windows;
    private readonly IMessageHistory _history;
    private readonly IAgentToolHistory _toolHistory;
    private readonly IHistorySearch _search;
    private readonly IConversationAssetAccess _assets;
    private readonly IModelModule _models;
    private readonly IModelProviderRuntime _providers;
    private readonly IMediaStore _media;
    private readonly IStructuredContentStore _contents;
    private readonly IMessageSender _sender;
    private readonly IConversationProfileService _profiles;
    private readonly AgentCapabilityDirectory _capabilities;
    private readonly AgentToolExecutor _executor;
    private readonly AgentOptions _options;
    private readonly PluginExecutionOptions _execution;
    private readonly Guid _runId;
    private readonly TimeProvider _clock;
    private int _pendingWakeAdmissions;
    private long _pendingBytes;
    private bool _accepting;
    private bool _opened;
    private bool _disposed;
    private string? _failure;
    public bool IsHealthy => Volatile.Read(ref _failure) is null;

    /// <summary>构造共享依赖，插件完成加载且配置有效之前不会登记唤醒。</summary>
    public AgentRuntime(IAgentStore store, IModelStore windows, IMessageHistory history, IAgentToolHistory toolHistory, IHistorySearch search, IConversationAssetAccess assets, IModelModule models, IModelProviderRuntime providers, IMediaStore media,
        IStructuredContentStore contents, IMessageSender sender, IConversationProfileService profiles, AgentCapabilityDirectory capabilities,
        AgentToolExecutor executor, AgentOptions options, PluginExecutionOptions execution, Guid runId, TimeProvider clock)
    {
        _store = store; _windows = windows; _history = history; _models = models; _providers = providers; _media = media; _contents = contents;
        _toolHistory = toolHistory; _search = search; _assets = assets;
        _sender = sender; _profiles = profiles; _capabilities = capabilities; _executor = executor; _options = options; _execution = execution; _runId = runId; _clock = clock;
    }

    /// <summary>宿主加载插件后显式开放任务入口，关闭模块时仍保持普通消息功能。</summary>
    public void Open()
    {
        lock (_capacityGate)
        {
            if (_opened || _stop.IsCancellationRequested) throw new InvalidOperationException("Agent 模块不能重复开放。");
            _opened = true;
            if (!_options.Enabled) return;
            _capabilities.Bind(this, Definitions(), _execution.ToolsAndProcessors.PerPluginConcurrency, _stop.Token);
            _accepting = true;
        }
    }

    /// <summary>容量和来源验证完成后在短协调边界登记唤醒，不把入口令牌传给已接受工作。</summary>
    public async Task<AgentWakeResult> WakeAsync(AgentWakeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Admission(AgentWakeState.Disabled, "Agent 模块未启用。");
        ContentValidation.Validate(request.CurrentContent);
        request.Cause.Validate();
        int bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.CurrentContent));
        if (bytes > _options.MaximumInputBytes) return Admission(AgentWakeState.CapacityExceeded, "唤醒内容超过单条上限。");
        lock (_capacityGate)
        {
            if (!_accepting || !IsHealthy) return Admission(AgentWakeState.NotReady, "Agent 尚未开放登记唤醒、已停止或基础设施不可用。");
            if (_pendingWakeAdmissions >= _options.WakeAdmissionCapacity || bytes > _options.WakeAdmissionBytes - _pendingBytes)
                return Admission(AgentWakeState.CapacityExceeded, "唤醒登记容量已满。");
            _pendingWakeAdmissions++; _pendingBytes += bytes;
        }
        try
        {
            AgentWakeRequest frozen = request with { CurrentContent = request.CurrentContent.Select(segment => segment.Freeze()).ToImmutableArray() };
            MessageSnapshot? message = await _history.GetAsync(request.MessageId, cancellationToken).ConfigureAwait(false);
            if (message is null || message.Direction != MessageDirection.Incoming || message.SenderId == message.Address.BotAccountId)
                return Admission(AgentWakeState.Rejected, "必须引用已保存的外部入站消息。");
            using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            await _coordination.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                if (!_accepting) return Admission(AgentWakeState.NotReady, "Agent 已停止登记唤醒。");
                AgentWakeResult? duplicate = await _store.FindWakeAsync(request.MessageId, admission.Token).ConfigureAwait(false);
                if (duplicate is not null) return duplicate;
                ConversationProfileSnapshot? profile = _profiles.TryResolve(message.Address);
                if (profile?.Profile.Agent.Enabled != true) return Admission(AgentWakeState.Disabled, "当前会话未启用 Agent。");
                _trees.TryGetValue(message.Address, out Tree? tree);
                if (tree is null && (!HasProvider(profile, profile.Profile.Agent.Models) || profile.Profile.Agent.Tools.Any(id => !_capabilities.CaptureTools().Any(tool => tool.Definition.Id == id))))
                    return Admission(AgentWakeState.NotReady, "当前配置所需的模型提供商或工具尚未加载。");
                if (tree is null && _trees.Count >= _options.MaximumTasks || tree is not null && !HasMailboxCapacity(tree.Root, bytes))
                    return Admission(AgentWakeState.CapacityExceeded, "活动任务或代理输入容量不足。");
                AgentWakeResult accepted = await _store.RegisterWakeAsync(new AgentWakeAdmission
                {
                    WakeRequest = frozen,
                    Profile = profile,
                    RunId = _runId,
                    NewTaskId = Guid.NewGuid(),
                    NewRootAgentId = Guid.NewGuid(),
                    Now = _clock.GetUtcNow()
                }, admission.Token).ConfigureAwait(false);
                if (!accepted.Registered || accepted.State == AgentWakeState.Duplicate) return accepted;
                // 已提交的输入由宿主负责；调用者此时取消不能使登记唤醒记录成为无人推进的孤儿。
                if (tree is null)
                {
                    AgentTaskSnapshot task = (await _store.GetTaskAsync(message.Address, accepted.TaskId!.Value, CancellationToken.None).ConfigureAwait(false))!;
                    AgentSnapshot root = (await _store.GetAgentsAsync(message.Address, task.Id, CancellationToken.None).ConfigureAwait(false)).Single();
                    tree = new Tree(task, root, _windows, _history, _stop.Token);
                    _trees.Add(message.Address, tree);
                }
                AgentWakeInput input = new() { MessageId = message.Id, SenderId = message.SenderId, ReceivedAt = message.ReceivedAt, OccurredAt = message.OccurredAt, Content = frozen.CurrentContent, Cause = frozen.Cause };
                tree.Root.Inputs.Enqueue(new Input(input, null, bytes)); tree.Root.MailboxBytes += bytes;
                tree.Root.Signal.Pulse(); StartUnderLock(tree, tree.Root);
                return accepted;
            }
            finally { _coordination.Release(); }
        }
        finally { lock (_capacityGate) { _pendingWakeAdmissions--; _pendingBytes -= bytes; } }
    }

    /// <summary>状态查询不等待模型完成，也不把停止标志当作真实资源退出。</summary>
    public async Task<AgentTaskStatus?> GetActiveAsync(MessageAddress conversation, CancellationToken cancellationToken = default)
    {
        await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AgentTaskSnapshot? task = await _store.GetActiveTaskAsync(conversation, cancellationToken).ConfigureAwait(false);
            if (task is null) return null;
            _trees.TryGetValue(conversation, out Tree? tree);
            return new AgentTaskStatus
            {
                Task = task,
                Agents = await _store.GetAgentsAsync(conversation, task.Id, cancellationToken).ConfigureAwait(false),
                ActiveWork = tree?.Working.ActiveCount ?? 0,
                PendingInputs = tree?.Nodes.Values.Sum(node => node.Inputs.Count) ?? 0,
                WaitingAgents = tree?.Nodes.Values.Count(node => node.Waiters > 0) ?? 0
            };
        }
        finally { _coordination.Release(); }
    }

    /// <summary>只有一个推进任务可拥有某个代理；有界任务数量来自根任务及代理创建预算。</summary>
    private void StartUnderLock(Tree tree, Node node)
    {
        if (node.Running || node.Closed || tree.Closing) return;
        node.Running = true;
        Task run = RunAsync(tree, node);
        lock (_capacityGate) _runs.Add(run);
        _ = ObserveAsync(run);
    }

    /// <summary>跟踪到推进函数实际返回，不通过取消提前删除任务引用。</summary>
    private async Task ObserveAsync(Task run)
    {
        try { await run.ConfigureAwait(false); }
        finally { lock (_capacityGate) _runs.Remove(run); }
    }

    /// <summary>每轮先消费已经登记唤醒输入；普通追加不能进入在途模型或未闭合的工具组。</summary>
    private async Task RunAsync(Tree tree, Node node)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                await _coordination.WaitAsync().ConfigureAwait(false);
                ConversationProfileSnapshot profile;
                try
                {
                    if (node.Closed) return;
                    if (node.StopDeadline.HasValue || node.Actual.IsCancellationRequested) break;
                    ConsumeUnderLock(tree, node);
                    profile = _profiles.Resolve(tree.Task.Conversation);
                    node.FinishTurn = false;
                    node.InTurn = true;
                }
                finally { _coordination.Release(); }
                Guid turnId = Guid.NewGuid();
                AgentOutputMode outputMode = profile.Profile.Agent.OutputMode;
                node.OutputMode = outputMode;
                AgentResult outcome = await AdvanceAsync(tree, node, turnId, profile, outputMode).ConfigureAwait(false);
                await CompleteAsync(tree, node, outcome).ConfigureAwait(false);
                await _coordination.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (node.Closed) return;
                    if (node.StopDeadline.HasValue || node.Actual.IsCancellationRequested) break;
                    if (node.Inputs.Count > 0 || node.RunningMail || HasUnconsumedResults(node)) continue;
                    return;
                }
                finally { _coordination.Release(); }
            }
            await SettleAsync(tree, node).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (node.Queued.IsCancellationRequested || node.Actual.IsCancellationRequested)
        {
            try { await SettleAsync(tree, node).ConfigureAwait(false); }
            catch (Exception exception) { await FailAsync(tree, node, "停止成果保存失败：" + exception.GetType().Name).ConfigureAwait(false); }
        }
        catch (Exception exception)
        {
            await FailAsync(tree, node, "Agent 推进失败：" + exception.GetType().Name).ConfigureAwait(false);
        }
        finally
        {
            await _coordination.WaitAsync().ConfigureAwait(false);
            try
            {
                node.Running = false;
                if (!node.Closed && !tree.Closing && (node.Inputs.Count > 0 || node.RunningMail || HasUnconsumedResults(node))) StartUnderLock(tree, node);
                await TryCloseUnderLockAsync(tree).ConfigureAwait(false);
            }
            catch (Exception exception) { _failure = "任务关闭记录失败：" + exception.GetType().Name; }
            finally { _coordination.Release(); }
        }
    }

    /// <summary>一个轮次可以多次调用模型，工具组完成前不开始下一次决策。</summary>
    private async Task<AgentResult> AdvanceAsync(Tree tree, Node node, Guid turnId, ConversationProfileSnapshot initial, AgentOutputMode mode)
    {
        ConversationProfileSnapshot profile = initial;
        ImmutableArray<Guid>.Builder invocations = ImmutableArray.CreateBuilder<Guid>();
        while (!node.StopDeadline.HasValue && !node.Actual.IsCancellationRequested)
        {
            (ImmutableArray<string> candidates, string instructions, bool stream) = ModelConfiguration(node, profile);
            ImmutableArray<AgentToolDescriptor> tools = AllowedTools(tree, node, profile);
            ModelInput input = await node.Conversation.CaptureAsync(profile, instructions, mode, node.Queued.Token).ConfigureAwait(false);
            OperationOrigin origin = new() { TaskId = tree.Task.Id, AgentId = node.Identity.Id, TurnId = turnId };
            ModelResult model;
            using (IDisposable permit = await tree.Working.EnterAsync(node.Identity.Id, node.Queued.Token).ConfigureAwait(false))
            {
                if (!await _store.ReserveCallsAsync(node.Identity.Id, 1, 0, true, false, node.Queued.Token).ConfigureAwait(false))
                    return new AgentResult { Complete = false, Error = "决策或任务模型预算耗尽，或任务已经停止。", ToolInvocations = invocations.ToImmutable() };
                await ActivityAsync(node, AgentActivity.RunningModel).ConfigureAwait(false);
                model = await _models.GenerateAsync(new ModelRequest
                {
                    Purpose = "agent",
                    Profile = profile,
                    Candidates = candidates,
                    Input = input,
                    Conversation = tree.Task.Conversation,
                    Origin = origin,
                    Stream = stream,
                    Tools = tools.Select(tool => new ModelTool { Name = tool.Definition.Name, Description = tool.Definition.Description + "\n返回：" + tool.Definition.OutputDescription, Parameters = tool.Definition.Parameters }).ToImmutableArray()
                }, cancellationToken: node.Actual.Token, admissionCancellationToken: node.Queued.Token).ConfigureAwait(false);
            }
            bool toolCalls = model.Content.Any(block => block.Kind == ModelBlockKind.ToolCall);
            node.Partial = new AgentResult
            {
                Content = PublicContent(model),
                Complete = model.Complete && !toolCalls,
                Error = model.Error,
                Unfinished = toolCalls ? "本次模型提出的工具请求尚未执行。" : null,
                ToolInvocations = invocations.ToImmutable()
            };
            AgentEntry modelEntry = await _store.AppendEntryAsync(node.Identity.Id, AgentEntryKind.ModelOutput, JsonSerializer.SerializeToElement(model), _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            if (model.State != ModelCallState.Succeeded || !model.Complete)
            {
                if (model.State == ModelCallState.RecordingFailed) throw new InvalidOperationException("模型结果记录失败，禁止自动推进。");
                return node.Partial with { Complete = false };
            }
            if (node.StopDeadline.HasValue || node.Actual.IsCancellationRequested) return node.Partial;
            if (!toolCalls)
            {
                node.Conversation.Append(model.ToAssistantMessage());
                if (node.Identity.ParentId is null && mode == AgentOutputMode.Direct && !string.IsNullOrWhiteSpace(model.Text))
                {
                    BatchSendResult sent = await _sender.SendAsync(OutgoingBatch.Single(tree.Task.Conversation, [SegmentValues.Text(model.Text)]) with { Origin = origin }, node.Queued.Token).ConfigureAwait(false);
                    await _store.AppendEntryAsync(node.Identity.Id, AgentEntryKind.Control, JsonSerializer.SerializeToElement(sent), _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                    if (!sent.Recorded) throw new InvalidOperationException("终答出站记录失败。");
                }
                return node.Partial;
            }
            await ActivityAsync(node, AgentActivity.RunningTool).ConfigureAwait(false);
            AgentToolContext context = ToolContext(tree, node, origin, profile);
            AgentToolGroupResult group = await _executor.ExecuteAsync(context, turnId, modelEntry.Id, model, tools, tree.Working, node.Queued.Token, node.Actual.Token).ConfigureAwait(false);
            invocations.AddRange(group.Results.Select(result => result.InvocationId));
            node.Partial = node.Partial with { ToolInvocations = invocations.ToImmutable(), Unfinished = group.CanContinue ? null : group.Error };
            if (!group.CanContinue)
            {
                if (node.StopDeadline.HasValue || node.Actual.IsCancellationRequested) return node.Partial;
                throw new InvalidOperationException(group.Error ?? "工具组没有完整闭合。");
            }
            node.Conversation.Append(model.ToAssistantMessage()); node.Conversation.Append(group.Message!);
            node.Partial = new AgentResult { Complete = true, ToolInvocations = invocations.ToImmutable() };
            if (node.FinishTurn) return node.Partial;
            await _coordination.WaitAsync().ConfigureAwait(false);
            try { ConsumeUnderLock(tree, node); profile = _profiles.Resolve(tree.Task.Conversation); }
            finally { _coordination.Release(); }
        }
        return node.Partial ?? new AgentResult { Complete = false };
    }

    /// <summary>任务权限上限与当前模板共同限制工具目录，短暂不可用的能力仍明确可识别。</summary>
    private ImmutableArray<AgentToolDescriptor> AllowedTools(Tree tree, Node node, ConversationProfileSnapshot profile)
    {
        IEnumerable<string> initial = node.Identity.ParentId is null ? tree.Task.InitialProfile.Tools
            : tree.Task.InitialProfile.Templates.Single(template => template.Id == node.Identity.TemplateId).Tools.Intersect(tree.Task.InitialProfile.DelegatedTools, StringComparer.Ordinal);
        SubagentTemplate? template = profile.Profile.Agent.Templates.SingleOrDefault(item => item.Id == node.Identity.TemplateId);
        IEnumerable<string> current = node.Identity.ParentId is null ? profile.Profile.Agent.Tools
            : (template?.Tools ?? []).Intersect(profile.Profile.Agent.DelegatedTools, StringComparer.Ordinal);
        HashSet<string> ids = initial.Intersect(current, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        return _capabilities.CaptureTools().Where(tool => ids.Contains(tool.Definition.Id)).Select(tool => tool.Definition.Id != AgentToolIds.Spawn ? tool : tool with
        {
            Definition = tool.Definition with
            {
                Description = tool.Definition.Description + " 用户允许的模板：" + JsonSerializer.Serialize(profile.Profile.Agent.Templates
                .Where(item => tree.Task.InitialProfile.Templates.Any(initialTemplate => initialTemplate.Id == item.Id))
                .Select(item => new Dictionary<string, object> { ["name"] = item.Id, ["purpose"] = item.Description, ["tools"] = item.Tools })) + "；默认模板：" + profile.Profile.Agent.DefaultTemplate
            }
        }).ToImmutableArray();
    }

    /// <summary>子代理模型与系统提示词由用户模板决定，工具参数不能覆盖连接配置。</summary>
    private static (ImmutableArray<string> Models, string Instructions, bool Stream) ModelConfiguration(Node node, ConversationProfileSnapshot profile)
    {
        if (!profile.Profile.Agent.Enabled) throw new InvalidOperationException("当前会话 Agent 已关闭。");
        if (node.Identity.ParentId is null) return (profile.Profile.Agent.Models, profile.Profile.Agent.Instructions, profile.Profile.Agent.Stream);
        SubagentTemplate template = profile.Profile.Agent.Templates.SingleOrDefault(item => item.Id == node.Identity.TemplateId)
            ?? throw new InvalidOperationException("子代理模板已撤销。");
        return (template.Models, template.Instructions, template.Stream);
    }

    /// <summary>工具上下文绑定宿主共享服务，普通插件拿不到任务树内部状态。</summary>
    private AgentToolContext ToolContext(Tree tree, Node node, OperationOrigin origin, ConversationProfileSnapshot profile) => new()
    {
        Origin = origin,
        Conversation = tree.Task.Conversation,
        Profile = profile,
        Models = _models,
        History = _history,
        AgentHistory = _store,
        ToolHistory = _toolHistory,
        Search = _search,
        Assets = _assets,
        Contents = _contents,
        Media = _media,
        Transcriptions = _models.Transcriptions,
        Clock = _clock,
        MaximumResultBytes = _options.MaximumToolResultBytes
    };

    /// <summary>冷启动不以空能力目录登记唤醒，短暂资格仅用于检查已有提供商是否发布。</summary>
    private bool HasProvider(ConversationProfileSnapshot profile, ImmutableArray<string> candidates)
    {
        foreach (string id in candidates)
        {
            using IModelProviderLease? lease = _providers.TryAcquire(profile.Providers[profile.Models[id].ProviderId].Protocol);
            if (lease is not null) return true;
        }
        return false;
    }

    /// <summary>模型推理和签名只留在模型轮次，不进入公开成果。</summary>
    private static ImmutableArray<ModelBlock> PublicContent(ModelResult model) => model.Content.Where(block => block.Kind == ModelBlockKind.Text).ToImmutableArray();

    /// <summary>登记唤醒容量失败不修改聊天入库结果。</summary>
    private static AgentWakeResult Admission(AgentWakeState state, string error) => new() { State = state, Error = error };

    /// <summary>普通邮箱包含明确输入与普通通信，控制通知另走有界成果列表。</summary>
    private bool HasMailboxCapacity(Node node, int bytes) => node.Inputs.Count + node.Mail.Count < _options.MailboxCapacity && bytes <= _options.MailboxBytes - node.MailboxBytes;

    /// <summary>活动记录跟随实际工作，停止状态不能被迟到的工作阶段覆盖。</summary>
    private Task ActivityAsync(Node node, AgentActivity activity)
        => _store.SetActivityAsync(node.Identity.Id, node.StopDeadline.HasValue ? AgentActivity.Stopping : activity, CancellationToken.None);

    /// <summary>健康快照只在短协调边界统计真实工作，不读取或序列化任务正文。</summary>
    public async Task<AgentRuntimeCounts> CaptureCountsAsync(CancellationToken cancellationToken)
    {
        await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int pending;
            lock (_capacityGate) pending = _pendingWakeAdmissions;
            return new AgentRuntimeCounts
            {
                ActiveTasks = _trees.Count,
                WorkingAgents = _trees.Values.Sum(tree => tree.Working.ActiveCount),
                WaitingAgents = _trees.Values.Sum(tree => tree.Nodes.Values.Count(node => node.Waiters > 0)),
                PendingWakeAdmissions = pending
            };
        }
        finally { _coordination.Release(); }
    }

    /// <summary>一个已经登记唤醒的输入或补充工作，不含插件委托与私有参数。</summary>
    private sealed record Input(AgentWakeInput? User, JsonElement? Data, int Bytes);

    /// <summary>任务树持有真实运行资源，持久化快照只用于身份与初始权限。</summary>
    private sealed class Tree
    {
        public AgentTaskSnapshot Task { get; }
        public Node Root { get; }
        public Dictionary<Guid, Node> Nodes { get; } = [];
        public AgentWorkLimiter Working { get; }
        public bool Closing { get; set; }
        public AgentTaskState? Outcome { get; set; }
        /// <summary>创建唯一主代理及树级工作额度，新的任务不继承旧工具轨迹。</summary>
        public Tree(AgentTaskSnapshot task, AgentSnapshot root, IModelStore windows, IMessageHistory history, CancellationToken lifetime)
        { Task = task; Root = new Node(root, task.Conversation, windows, history, lifetime); Nodes.Add(root.Id, Root); Working = new AgentWorkLimiter(task.InitialProfile.Budget.WorkingAgents); }
    }

    /// <summary>单代理只有一个推进者；状态、邮箱、消费位置和取消资源由任务树统一拥有。</summary>
    private sealed class Node
    {
        public AgentSnapshot Identity { get; }
        public AgentConversation Conversation { get; }
        public Queue<Input> Inputs { get; } = [];
        public Queue<Input> Mail { get; } = [];
        public List<(Guid Agent, long Revision)> Notices { get; } = [];
        public Dictionary<Guid, long> Consumed { get; } = [];
        public Dictionary<long, AgentResult> Results { get; } = [];
        public Signal Signal { get; } = new();
        public CancellationTokenSource Queued { get; }
        public CancellationTokenSource Actual { get; }
        public DateTimeOffset? StopDeadline { get; set; }
        public ITimer? Timer { get; set; }
        public Task Cancellation { get; set; } = Task.CompletedTask;
        public Task QueueCancellation { get; set; } = Task.CompletedTask;
        public bool MaySettle { get; set; }
        public bool Running { get; set; }
        public bool InTurn { get; set; }
        public AgentOutputMode OutputMode { get; set; }
        public bool Closed { get; set; }
        public bool FinishTurn { get; set; }
        public bool RunningMail { get; set; }
        public int Waiters { get; set; }
        public long MailboxBytes { get; set; }
        public long Revision { get; set; }
        public AgentResult? Partial { get; set; }
        /// <summary>根代理观察当前会话，子代理只取得明确下达的任务资料。</summary>
        public Node(AgentSnapshot identity, MessageAddress address, IModelStore windows, IMessageHistory history, CancellationToken lifetime)
        {
            Identity = identity; Conversation = new AgentConversation(address, windows, history, identity.ParentId is null);
            Queued = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Actual = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }
    }

    /// <summary>每次变化替换一个异步信号，注册和观察均在任务协调边界内，不依赖轮询。</summary>
    private sealed class Signal
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Changed => _changed.Task;
        /// <summary>唤醒当前等待者，新等待捕获下一次变化。</summary>
        public void Pulse() { TaskCompletionSource previous = _changed; _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
    }
}
