using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>停止请求、一次整理及真实退出统一归属运行器，插件停止期限独立管理。</summary>
public sealed partial class AgentRuntime
{
    /// <summary>从原消息核对当前管理员；首次唤醒者不获得运行控制权。</summary>
    public async Task<AgentControlResult> RequestStopAsync(AgentStopRequest request, CancellationToken cancellationToken = default)
    {
        MessageSnapshot? message = await _history.GetAsync(request.MessageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Direction != MessageDirection.Incoming) return new AgentControlResult { Error = "停止必须引用真实用户消息。" };
        await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_trees.TryGetValue(message.Address, out Tree? tree)) return new AgentControlResult { Error = "当前会话没有活动任务。" };
            ConversationProfileSnapshot current = _profiles.Resolve(message.Address);
            bool admin = current.Profile.Agent.Administrators.Contains(message.SenderId, StringComparer.Ordinal);
            if (!admin) return new AgentControlResult { TaskId = tree.Task.Id, Error = "只有当前配置的 Bot 管理员可以停止运行。" };
            if (!tree.Nodes.TryGetValue(request.AgentId ?? tree.Root.Identity.Id, out Node? target)) return new AgentControlResult { TaskId = tree.Task.Id, Error = "目标不属于当前任务。" };
            await StopUnderLockAsync(tree, target, request.Emergency, cancellationToken).ConfigureAwait(false);
            return new AgentControlResult { Accepted = true, TaskId = tree.Task.Id };
        }
        finally { _coordination.Release(); }
    }

    /// <summary>一个子树共享首次停止期限，重复请求与祖先停止只能使期限更早。</summary>
    private async Task StopUnderLockAsync(Tree tree, Node target, bool emergency, CancellationToken token)
    {
        if (target.Closed) return;
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset requested = emergency ? now : now + tree.Task.InitialProfile.GracefulStopTimeout;
        DateTimeOffset deadline = target.StopDeadline is DateTimeOffset existing && existing < requested ? existing : requested;
        Node[] subtree = tree.Nodes.Values.Where(node => !node.Closed && (node == target || Descendant(tree, node.Identity.Id, target.Identity.Id))).ToArray();
        await _store.RequestStopAsync(tree.Task.Id, target.Identity.Id, subtree.Select(node => node.Identity.Id).ToImmutableArray(), now, deadline < now ? now : deadline, token).ConfigureAwait(false);
        if (target == tree.Root) tree.Outcome = AgentTaskState.Cancelled;
        foreach (Node node in subtree)
        {
            bool first = node.StopDeadline is null;
            node.StopDeadline = node.StopDeadline is DateTimeOffset previous && previous < deadline ? previous : deadline;
            if (first) node.MaySettle = !emergency && node == target;
            else if (emergency || node != target) node.MaySettle = false;
            node.QueueCancellation = node.Queued.CancelAsync();
            node.Signal.Pulse();
            if (node.Timer is not null) await node.Timer.DisposeAsync().ConfigureAwait(false);
            TimeSpan remaining = node.StopDeadline.Value - now;
            if (remaining <= TimeSpan.Zero || emergency) node.Cancellation = node.Actual.CancelAsync();
            else node.Timer = _clock.CreateTimer(static state =>
            {
                Node stopped = (Node)state!;
                stopped.Cancellation = stopped.Actual.CancelAsync();
            }, node, remaining, Timeout.InfiniteTimeSpan);
            if (!node.Running && !node.Closed) StartUnderLock(tree, node);
        }
    }

    /// <summary>成果先提交，再把引用通知交给父代理；明确取消后的迟到成果只保存。</summary>
    private async Task CompleteAsync(Tree tree, Node node, AgentResult result, bool closed = false)
    {
        closed |= node == tree.Root && !result.Complete && result.Error is not null && node.StopDeadline is null;
        AgentSnapshot saved = await _store.CompleteTurnAsync(node.Identity.Id, result, _clock.GetUtcNow(), closed, CancellationToken.None).ConfigureAwait(false);
        await _coordination.WaitAsync().ConfigureAwait(false);
        try
        {
            node.InTurn = false;
            node.Revision = saved.ResultRevision; node.Results.Add(saved.ResultRevision, result); node.Partial = result; node.Closed = closed;
            // 同树等待不限父子关系；信号仅让已登记的等待重新观察，不唤醒空闲代理模型。
            foreach (Node waiting in tree.Nodes.Values.Where(waiting => waiting.Waiters > 0)) waiting.Signal.Pulse();
            if (node.Identity.ParentId is Guid parentId)
            {
                Node parent = tree.Nodes[parentId];
                await _store.AppendResultNotificationAsync(parentId, node.Identity.Id, saved.ResultRevision, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                parent.Notices.Add((node.Identity.Id, saved.ResultRevision)); parent.Signal.Pulse();
                if (parent.StopDeadline is null && !parent.Actual.IsCancellationRequested) StartUnderLock(tree, parent);
            }
            if (node == tree.Root && !result.Complete && result.Error is not null && node.StopDeadline is null)
            {
                tree.Outcome = AgentTaskState.Failed;
                foreach (Node child in tree.Nodes.Values.Where(child => child.Identity.ParentId == node.Identity.Id && !child.Closed).ToArray())
                    await StopUnderLockAsync(tree, child, false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _coordination.Release(); }
    }

    /// <summary>普通停止只允许目标一次无工具整理，共享剩余期限、模型预算与原生续接约束。</summary>
    private async Task SettleAsync(Tree tree, Node node)
    {
        AgentResult result = node.Partial ?? new AgentResult { Complete = false, Unfinished = "任务在取得结果之前停止。" };
        if (node.MaySettle && !node.Actual.IsCancellationRequested && node.StopDeadline > _clock.GetUtcNow())
        {
            node.MaySettle = false;
            try
            {
                ConversationProfileSnapshot profile = _profiles.Resolve(tree.Task.Conversation);
                (ImmutableArray<string> candidates, string instructions, bool stream) = ModelConfiguration(node, profile);
                string? selected = candidates.FirstOrDefault(id => node.Conversation.CanUse(profile.Models[id], profile.Providers[profile.Models[id].ProviderId]));
                if (selected is not null && await _store.ReserveCallsAsync(node.Identity.Id, 1, 0, false, true, node.Actual.Token).ConfigureAwait(false))
                {
                    ModelInput input = await node.Conversation.CaptureAsync(profile, instructions, node.OutputMode, node.Actual.Token).ConfigureAwait(false);
                    input = input with
                    {
                        Items = input.Items.Add(new ModelTurnInput
                        {
                            Message = new ModelMessage
                            {
                                Role = "user",
                                Content =
                        [ModelBlock.TextBlock("任务已请求停止。只整理已经取得的成果、未完成事项和不确定的副作用，不调用工具，不新增公开回复。以下是已有成果资料：" + JsonSerializer.Serialize(result))]
                            }
                        })
                    };
                    using IDisposable permit = await tree.Working.EnterAsync(node.Identity.Id, node.Actual.Token).ConfigureAwait(false);
                    ModelResult summary = await _models.GenerateAsync(new ModelRequest
                    {
                        Purpose = "agent_settlement",
                        Conversation = tree.Task.Conversation,
                        Origin = new OperationOrigin { TaskId = tree.Task.Id, AgentId = node.Identity.Id, TurnId = Guid.NewGuid() },
                        Profile = profile with { Profile = profile.Profile with { Execution = profile.Profile.Execution with { Rounds = 1, RoundDelay = TimeSpan.Zero } } },
                        Candidates = [selected],
                        Input = input,
                        Stream = stream
                    }, cancellationToken: node.Actual.Token).ConfigureAwait(false);
                    await _store.AppendEntryAsync(node.Identity.Id, AgentEntryKind.ModelOutput, JsonSerializer.SerializeToElement(summary), _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                    if (summary.State == ModelCallState.Succeeded && summary.Complete && !summary.Content.Any(block => block.Kind == ModelBlockKind.ToolCall) && !string.IsNullOrWhiteSpace(summary.Text))
                        result = result with { Content = PublicContent(summary), Complete = false, Unfinished = "任务按请求停止，整理仅描述停止前的成果。" };
                }
            }
            catch (OperationCanceledException) when (node.Actual.IsCancellationRequested) { }
            catch (Exception exception) { result = result with { Unfinished = "停止整理未完成：" + exception.GetType().Name }; }
        }
        await Task.WhenAll(node.Cancellation, node.QueueCancellation).ConfigureAwait(false);
        await CompleteAsync(tree, node, result, true).ConfigureAwait(false);
    }

    /// <summary>失败不丢弃已有结果，也不能留下无人管理的后代工作。</summary>
    private async Task FailAsync(Tree tree, Node node, string error)
    {
        try
        {
            await _coordination.WaitAsync().ConfigureAwait(false);
            try
            {
                if (node == tree.Root) tree.Outcome = AgentTaskState.Failed;
                foreach (Node child in tree.Nodes.Values.Where(child => child.Identity.ParentId == node.Identity.Id && !child.Closed).ToArray())
                    await StopUnderLockAsync(tree, child, false, CancellationToken.None).ConfigureAwait(false);
                node.MaySettle = false;
            }
            finally { _coordination.Release(); }
            await CompleteAsync(tree, node, (node.Partial ?? new AgentResult()) with { Complete = false, Error = error }, true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _failure = "Agent 失败记录无法保存：" + exception.GetType().Name;
            await _coordination.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (Node active in tree.Nodes.Values)
                { active.MaySettle = false; active.Cancellation = active.Actual.CancelAsync(); active.QueueCancellation = active.Queued.CancelAsync(); }
            }
            finally { _coordination.Release(); }
        }
    }

    /// <summary>所有真实推进结束且没有待处理工作时才释放主代理归属，空闲子代理一起关闭。</summary>
    private async Task TryCloseUnderLockAsync(Tree tree)
    {
        if (tree.Closing || tree.Nodes.Values.Any(node => node.Running)) return;
        if (tree.Outcome is null && tree.Nodes.Values.Any(node => !node.Closed && (node.Inputs.Count > 0 || node.RunningMail || HasUnconsumedResults(node)))) return;
        tree.Closing = true;
        foreach (Node node in tree.Nodes.Values)
        {
            node.Closed = true; node.Signal.Pulse();
            if (node.Timer is not null) await node.Timer.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(node.Cancellation, node.QueueCancellation).ConfigureAwait(false);
        }
        await _store.FinishTaskAsync(tree.Task.Id, tree.Outcome ?? (_stop.IsCancellationRequested ? AgentTaskState.Interrupted : AgentTaskState.Completed), tree.Root.Partial?.Error, _clock.GetUtcNow(), true, CancellationToken.None).ConfigureAwait(false);
        _trees.Remove(tree.Task.Conversation);
        foreach (Node node in tree.Nodes.Values) { node.Queued.Dispose(); node.Actual.Dispose(); }
        tree.Working.Dispose();
    }

    /// <summary>宿主关闭立即取消真实工作，不新增整理模型请求；存储由外层等待退出后关闭。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_capacityGate) _accepting = false;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _coordination.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (Tree tree in _trees.Values.ToArray())
            {
                await StopUnderLockAsync(tree, tree.Root, true, CancellationToken.None).ConfigureAwait(false);
                tree.Outcome = AgentTaskState.Interrupted;
                await TryCloseUnderLockAsync(tree).ConfigureAwait(false);
            }
        }
        finally { _coordination.Release(); }
        Task[] runs;
        lock (_capacityGate) runs = _runs.ToArray();
        await Task.WhenAll(runs).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>停止和全部实际工作退出后释放宿主取消源，重复释放不重复控制任务。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false); _disposed = true; _stop.Dispose();
    }
}
