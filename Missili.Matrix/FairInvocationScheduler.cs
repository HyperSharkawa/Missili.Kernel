using System.Threading.Channels;
using Missili.Abstractions;

namespace Missili.Matrix;

/// <summary>按插件轮流选择可运行调用；尚无插件额度的任务不占全局执行槽。</summary>
public sealed class FairInvocationScheduler : IInvocationScheduler
{
    private readonly object _gate = new();
    private readonly int _concurrency;
    private readonly SemaphoreSlim _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(1);
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly LinkedList<Group> _rotation = new();
    private readonly HashSet<Work> _active = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private bool _closing;

    /// <summary>设置全局运行上限与有限排队容量，立即启动轻量调度循环。</summary>
    public FairInvocationScheduler(int concurrency, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _concurrency = concurrency;
        _capacity = new SemaphoreSlim(capacity, capacity);
        _pump = Task.Run(PumpAsync);
    }

    public int ActiveCount { get { lock (_gate) return _active.Count; } }
    public int PendingCount { get { lock (_gate) return _groups.Values.Sum(group => group.Pending.Count); } }

    /// <summary>先等待排队容量，再创建工作；返回的内层任务表示实际调用结果。</summary>
    public Task<Task<T>> EnqueueAsync<T>(string pluginId, int maxConcurrency, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        EnqueueVersionedAsync(pluginId, maxConcurrency, 0, operation, cancellationToken);

    /// <summary>配置修订可更新后续启动额度；旧请求重试不能回退新的并发配置。</summary>
    public async Task<Task<T>> EnqueueVersionedAsync<T>(string pluginId, int maxConcurrency, long quotaRevision, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(operation);
        if (maxConcurrency < 1 || maxConcurrency > _concurrency) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _capacity.WaitAsync(admission.Token).ConfigureAwait(false);
        Work<T> work;
        lock (_gate)
        {
            if (_closing)
            {
                _capacity.Release();
                throw new OperationCanceledException(_stop.Token);
            }
            if (!_groups.TryGetValue(pluginId, out Group? group))
            {
                group = new Group { Id = pluginId, Limit = maxConcurrency, Revision = quotaRevision };
                group.Rotation = _rotation.AddLast(group);
                _groups.Add(pluginId, group);
            }
            else if (quotaRevision > group.Revision)
            { group.Limit = maxConcurrency; group.Revision = quotaRevision; }
            else if (group.Limit != maxConcurrency && quotaRevision == group.Revision)
            {
                _capacity.Release();
                throw new InvalidOperationException("同一插件的并发限制必须一致。");
            }
            work = new Work<T>(this, group, operation, cancellationToken);
            work.Node = group.Pending.AddLast(work);
        }
        CancellationTokenRegistration registration = cancellationToken.Register(static state => ((Work)state!).Cancel(), work);
        work.SetRegistration(registration);
        _wake.Writer.TryWrite(0);
        return work.Completion.Task;
    }

    /// <summary>有新任务或完成通知时选取可运行工作，空闲时异步等待。</summary>
    private async Task PumpAsync()
    {
        await foreach (byte signal in _wake.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            while (true)
            {
                Work? work;
                lock (_gate) work = _closing || _active.Count >= _concurrency ? null : TakeReady();
                if (work is null) break;
                _ = Task.Run(() => ExecuteAsync(work));
            }
        }
    }

    /// <summary>在锁内轮流检查每个插件一次，防止饱和插件独占调度。</summary>
    private Work? TakeReady()
    {
        int remaining = _rotation.Count;
        while (remaining-- > 0)
        {
            LinkedListNode<Group> node = _rotation.First!;
            _rotation.RemoveFirst();
            _rotation.AddLast(node);
            Group group = node.Value;
            if (group.Active >= group.Limit || group.Pending.Count == 0) continue;
            Work work = group.Pending.First!.Value;
            group.Pending.RemoveFirst();
            work.Node = null;
            group.Active++;
            _active.Add(work);
            _capacity.Release();
            return work;
        }
        return null;
    }

    /// <summary>执行完成后释放真实活动额度，结果取消不等于工作已经退出。</summary>
    private async Task ExecuteAsync(Work work)
    {
        try { await work.ExecuteAsync(_stop.Token).ConfigureAwait(false); }
        finally
        {
            work.FinishRegistration();
            lock (_gate)
            {
                _active.Remove(work);
                work.Group.Active--;
                RemoveEmptyGroup(work.Group);
                if (_closing && _active.Count == 0) _drained.TrySetResult();
            }
            _wake.Writer.TryWrite(0);
        }
    }

    /// <summary>立即移除已取消的排队工作，避免旧插件委托等待其他慢调用才释放。</summary>
    private void CancelQueued(Work work)
    {
        lock (_gate)
        {
            if (work.Node is null) return;
            work.Group.Pending.Remove(work.Node);
            work.Node = null;
            _capacity.Release();
            work.CancelPending();
            RemoveEmptyGroup(work.Group);
        }
        work.FinishRegistration();
    }

    /// <summary>没有排队和活动工作时移除分组，不累计卸载过的插件标识。</summary>
    private void RemoveEmptyGroup(Group group)
    {
        if (group.Active != 0 || group.Pending.Count != 0) return;
        _groups.Remove(group.Id);
        _rotation.Remove(group.Rotation!);
    }

    /// <summary>取消未执行工作并等待实际活动调用退出，不把取消令牌当作强制中止。</summary>
    public async ValueTask DisposeAsync()
    {
        List<Work> pending;
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            pending = _groups.Values.SelectMany(group => group.Pending).ToList();
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (Work work in pending) CancelQueued(work);
        lock (_gate)
        {
            if (_active.Count == 0) _drained.TrySetResult();
        }
        _wake.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>插件队列和额度在同一调度锁下维护。</summary>
    private sealed class Group
    {
        public required string Id { get; init; }
        public required int Limit { get; set; }
        public long Revision { get; set; }
        public int Active { get; set; }
        public LinkedList<Work> Pending { get; } = new();
        public LinkedListNode<Group>? Rotation { get; set; }
    }

    /// <summary>排队工作的公共归属与取消登记，不存储完成历史。</summary>
    private abstract class Work
    {
        private readonly FairInvocationScheduler _owner;
        private readonly object _registrationGate = new();
        private CancellationTokenRegistration _registration;
        private bool _finished;
        public Group Group { get; }
        public LinkedListNode<Work>? Node { get; set; }

        /// <summary>保存固定归属，供取消回调直接从队列移除。</summary>
        protected Work(FairInvocationScheduler owner, Group group) { _owner = owner; Group = group; }
        /// <summary>取消回调只请求移除尚未执行的工作。</summary>
        public void Cancel() => _owner.CancelQueued(this);
        /// <summary>处理注册与同步取消之间的竞争，完成后不再保留令牌回调。</summary>
        public void SetRegistration(CancellationTokenRegistration registration)
        {
            lock (_registrationGate)
            {
                if (_finished) registration.Unregister();
                else _registration = registration;
            }
        }
        /// <summary>注销回调而不等待当前取消回调自身，防止锁互等。</summary>
        public void FinishRegistration()
        {
            lock (_registrationGate) { _finished = true; _registration.Unregister(); }
        }
        /// <summary>执行实际工作，直到工作本身退出。</summary>
        public abstract Task ExecuteAsync(CancellationToken stopToken);
        /// <summary>完成尚未启动工作的取消结果并释放委托。</summary>
        public abstract void CancelPending();
    }

    /// <summary>类型化结果容器，运行结束后清除操作委托。</summary>
    private sealed class Work<T> : Work
    {
        private Func<CancellationToken, Task<T>>? _operation;
        private readonly CancellationToken _caller;
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>创建已获得排队容量的调用。</summary>
        public Work(FairInvocationScheduler owner, Group group, Func<CancellationToken, Task<T>> operation, CancellationToken caller) : base(owner, group)
        { _operation = operation; _caller = caller; }

        /// <summary>链接宿主与调用者取消，捕获结果但始终等待实际委托退出。</summary>
        public override async Task ExecuteAsync(CancellationToken stopToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_caller, stopToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                T result = await _operation!(linked.Token).ConfigureAwait(false);
                Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { Completion.TrySetCanceled(linked.Token); }
            catch (Exception exception) { Completion.TrySetException(exception); }
            finally { _operation = null; }
        }

        /// <summary>排队期间取消时从不执行委托。</summary>
        public override void CancelPending()
        {
            _operation = null;
            Completion.TrySetCanceled(_caller.IsCancellationRequested ? _caller : new CancellationToken(true));
        }
    }
}
