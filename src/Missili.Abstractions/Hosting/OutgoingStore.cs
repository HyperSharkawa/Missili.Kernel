using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>只有宿主持有出站写入能力，公开插件仅取得查询契约。</summary>
public interface IOutgoingStore : IOutgoingHistory
{
    /// <summary>接管草稿前保存批次，失败时不开始变换或网络动作。</summary>
    Task CreateBatchAsync(OutgoingBatch batch, Guid runId, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>执行处理器前保存本批配置及代次；缺失或失败也保留完整排障依据。</summary>
    Task SetBatchProcessingAsync(Guid batchId, OutgoingProcessingSnapshot processing, CancellationToken cancellationToken);
    /// <summary>记录冻结的最终内容和本批处理器身份，审批之后不能再修改正文。</summary>
    Task SetBatchContentAsync(Guid batchId, ImmutableArray<MessageDraft> messages, ImmutableArray<OutgoingProcessorDescriptor> processors, CancellationToken cancellationToken);
    /// <summary>在发送前保存审批结论，写入失败不调用平台。</summary>
    Task SetBatchApprovalAsync(Guid batchId, ToolApprovalRequest? request, ToolApprovalResult approval, CancellationToken cancellationToken);
    /// <summary>保存最终批次结果，已经产生的回执不能被写入故障抹掉。</summary>
    Task FinishBatchAsync(BatchSendResult result, DateTimeOffset now, CancellationToken cancellationToken);
}
