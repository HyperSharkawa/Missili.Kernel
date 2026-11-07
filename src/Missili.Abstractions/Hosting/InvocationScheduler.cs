using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>公平调度契约；入队与实际完成分开，调用资格仍由 Nexus 管理。</summary>
public interface IInvocationScheduler : IAsyncDisposable
{
    /// <summary>等待有限入队容量后返回完成任务；等待插件额度不占活动槽。</summary>
    Task<Task<T>> EnqueueAsync<T>(string pluginId, int maxConcurrency, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
