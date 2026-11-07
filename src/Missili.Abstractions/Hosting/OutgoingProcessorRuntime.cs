using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>处理器资格只覆盖当前实际执行，审批和传输不保活已完成处理器。</summary>
public interface IOutgoingProcessorLease : IDisposable
{
    CancellationToken LifetimeToken { get; }
    /// <summary>调用固定代次的处理器，返回后宿主复制共享结果。</summary>
    Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken);
}

/// <summary>出站目录提供纯数据快照及资格，不拥有独立的插件启停状态。</summary>
public interface IOutgoingProcessorRuntime
{
    /// <summary>捕获可配置的稳定处理器目录，包括暂时不可用的已知项。</summary>
    ImmutableArray<OutgoingProcessorDescriptor> CaptureProcessors();
    /// <summary>旧代次不可用时返回空值，调用方必须结束本批而不是跳过它。</summary>
    IOutgoingProcessorLease? TryAcquireProcessor(OutgoingProcessorDescriptor descriptor);
}
