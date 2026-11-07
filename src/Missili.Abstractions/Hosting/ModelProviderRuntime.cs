using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>宿主调用资格，流和诊断退出前不得释放。</summary>
public interface IModelProviderLease : IDisposable
{
    Guid Generation { get; }
    CancellationToken LifetimeToken { get; }
    /// <summary>在固定代次中准备并计量请求，计量期间不占模型网络额度。</summary>
    Task<long> MeasureAsync(ModelAttemptRequest request, CancellationToken cancellationToken);
    /// <summary>在固定代次执行一次模型请求。</summary>
    Task<ModelProtocolResult> ExecuteAsync(ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken cancellationToken);
}

/// <summary>只向宿主协调器开放调用资格，不公开实际驱动实例。</summary>
public interface IModelProviderRuntime
{
    /// <summary>捕获可用代次；不可用立即返回空，不等待重载。</summary>
    IModelProviderLease? TryAcquire(ModelProtocol protocol);
}
