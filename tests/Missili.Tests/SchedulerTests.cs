using Missili.Matrix;
using Xunit;

namespace Missili.Tests;

/// <summary>用受控任务验证公平性、额度和取消，不通过休眠推断执行先后。</summary>
public sealed class SchedulerTests
{
    /// <summary>队列容量之外的提交等待，不会额外启动业务任务。</summary>
    [Fact]
    public async Task FullQueueAppliesBackpressureBeforeAllocatingWork()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FairInvocationScheduler scheduler = new(1, 1);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> active = await scheduler.EnqueueAsync("a", 1, async cancellation =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 1;
        }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Task<int> queued = await scheduler.EnqueueAsync("a", 1, _ => Task.FromResult(2), token);
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<Task<int>> outside = scheduler.EnqueueAsync("b", 1, _ => Task.FromResult(3), admission.Token);
        Assert.False(outside.IsCompleted);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal(1, scheduler.ActiveCount);
        await admission.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outside);
        release.TrySetResult();
        Assert.Equal(1, await active);
        Assert.Equal(2, await queued);
    }

    /// <summary>一个单并发插件占用额度时，其他插件仍能获得空闲全局槽。</summary>
    [Fact]
    public async Task SaturatedPluginDoesNotConsumeOtherPluginsSlots()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FairInvocationScheduler scheduler = new(2, 4);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> a = await scheduler.EnqueueAsync("a", 1, async cancellation =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 1;
        }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Task<int> waiting = await scheduler.EnqueueAsync("a", 1, _ => Task.FromResult(2), token);
        Task<int> other = await scheduler.EnqueueAsync("b", 1, _ => Task.FromResult(3), token);
        Assert.Equal(3, await other.WaitAsync(TimeSpan.FromSeconds(5), token));
        Assert.False(waiting.IsCompleted);
        release.TrySetResult();
        Assert.Equal(1, await a.WaitAsync(TimeSpan.FromSeconds(5), token));
        Assert.Equal(2, await waiting.WaitAsync(TimeSpan.FromSeconds(5), token));
    }

    /// <summary>排队取消立即移除并释放容量，不需要等待活动插件结束。</summary>
    [Fact]
    public async Task CancellationRemovesQueuedInvocationImmediately()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FairInvocationScheduler scheduler = new(1, 1);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> running = await scheduler.EnqueueAsync("a", 1, async cancellation =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 1;
        }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        using CancellationTokenSource queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        int executions = 0;
        Task<int> queued = await scheduler.EnqueueAsync("a", 1, _ => Task.FromResult(Interlocked.Increment(ref executions)), queuedCancellation.Token);
        await queuedCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(0, executions);
        release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5), token);
    }
}
