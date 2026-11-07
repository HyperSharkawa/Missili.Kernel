using System.Threading.Channels;

namespace Missili.Persistence;

/// <summary>将 SQLite 同步调用移至固定工作循环，不为每个排队操作启动线程。</summary>
internal sealed class DatabaseExecutor : IAsyncDisposable
{
    private readonly Channel<Action> _queue;
    private readonly Task[] _workers;

    /// <summary>创建有界执行器；工作数控制同步数据库访问的并发。</summary>
    public DatabaseExecutor(int capacity, int workers)
    {
        _queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = workers == 1,
            AllowSynchronousContinuations = false
        });
        _workers = new Task[workers];
        for (int index = 0; index < workers; index++) _workers[index] = Task.Run(ConsumeAsync);
    }

    /// <summary>等待入队容量后执行操作；取消在操作开始前检查，已开始的结果不会被遗忘。</summary>
    public async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(Execute, cancellationToken).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);

        /// <summary>在固定工作循环中捕获本操作结果，保证失败不会终止队列。</summary>
        void Execute()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>按队列持续执行，写端完成后退出。</summary>
    private async Task ConsumeAsync()
    {
        await foreach (Action operation in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) operation();
    }

    /// <summary>停止接收新工作并等待已入队操作完成。</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
    }
}
