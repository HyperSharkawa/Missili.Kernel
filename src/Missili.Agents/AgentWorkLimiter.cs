namespace Missili.Agents;

/// <summary>单棵任务树的实际工作额度；等待和空闲不持有资格。</summary>
public sealed class AgentWorkLimiter : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maximum;
    private readonly Dictionary<Guid, int> _active = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ActiveCount { get { lock (_gate) return _active.Count; } }

    /// <summary>容量来自根任务初始预算，不随后续配置扩大。</summary>
    public AgentWorkLimiter(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        _maximum = maximum;
    }

    /// <summary>排队等待不计入实际工作数量，取得资格后由调用者跟踪至真实退出。</summary>
    public async Task<IDisposable> EnterAsync(Guid agentId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 同一代理的并行工具共享一个工作身份，不能把工具数量误算为代理数量。
                if (_active.TryGetValue(agentId, out int count) || _active.Count < _maximum)
                {
                    _active[agentId] = count + 1;
                    PulseUnderLock();
                    return new Permit(this, agentId);
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>只在整棵任务的真实工作全部退出后释放信号量。</summary>
    public void Dispose()
    {
        lock (_gate)
            if (_active.Count != 0) throw new InvalidOperationException("实际代理工作尚未退出。");
    }

    /// <summary>身份变化唤醒等待同一代理或空余额度的调用，所有等待继续核对真实容量。</summary>
    private void PulseUnderLock()
    {
        TaskCompletionSource previous = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>一个实际操作最多归还一次容量。</summary>
    private sealed class Permit : IDisposable
    {
        private AgentWorkLimiter? _owner;
        private readonly Guid _agentId;
        /// <summary>保存取得资格的唯一所有者。</summary>
        public Permit(AgentWorkLimiter owner, Guid agentId) { _owner = owner; _agentId = agentId; }
        /// <summary>实际操作收尾后才归还额度，不由取消请求提前释放。</summary>
        public void Dispose()
        {
            AgentWorkLimiter? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._gate)
            {
                int remaining = owner._active[_agentId] - 1;
                if (remaining == 0) { owner._active.Remove(_agentId); owner.PulseUnderLock(); }
                else owner._active[_agentId] = remaining;
            }
        }
    }
}
