using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>消息内容草稿不包含目标会话，插件后处理不能改变发送对象。</summary>
public sealed record MessageDraft
{
    public required ImmutableArray<MessageSegment> Segments { get; init; }
    public JsonElement? Metadata { get; init; }

    /// <summary>取得草稿所有权，生成插件释放后仍可继续处理共享数据。</summary>
    public MessageDraft Freeze() => this with
    { Segments = Segments.Select(segment => segment.Freeze()).ToImmutableArray(), Metadata = Metadata?.Clone() };
}

/// <summary>批次状态描述整体进度，单条消息的真实平台状态始终保留在回执中。</summary>
public enum BatchSendState { Pending, Processing, Reviewing, Sending, Completed, PartiallySent, Unconfirmed, Filtered, Rejected, Failed, Cancelled, Interrupted }

/// <summary>一次发送请求固定目标及归属，处理器只变更其中的消息内容。</summary>
public sealed record OutgoingBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MessageAddress Address { get; init; }
    public required ImmutableArray<MessageDraft> Messages { get; init; }
    public string Purpose { get; init; } = "reply";
    public OperationOrigin Origin { get; init; } = new();
    public string? SourceToolId { get; init; }
    public long? TriggerMessageId { get; init; }

    /// <summary>单条调用也使用同一批次接口，后续处理仍然可以拆分它。</summary>
    public static OutgoingBatch Single(MessageAddress address, ImmutableArray<MessageSegment> segments, string purpose = "reply") => new()
    { Address = address, Messages = [new MessageDraft { Segments = segments }], Purpose = purpose };

    /// <summary>接管插件返回的共享草稿后不再引用调用方 JsonDocument。</summary>
    public OutgoingBatch Freeze() => this with { Messages = Messages.Select(message => message.Freeze()).ToImmutableArray() };
}

/// <summary>发送结果保留已尝试条目的顺序，剩余条目不会由读取或迟到回执自动补发。</summary>
public sealed record BatchSendResult
{
    public required Guid BatchId { get; init; }
    public required BatchSendState State { get; init; }
    public int TotalMessages { get; init; }
    public ImmutableArray<SendResult> Messages { get; init; } = [];
    public bool Recorded { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
    public int UnattemptedFrom => Messages.Length;
    public bool Confirmed => State == BatchSendState.Completed && Recorded && Messages.All(message => message.State == DeliveryState.Confirmed && message.Recorded);
}

/// <summary>纯内容变换描述，稳定身份复用已有处理器键。</summary>
public sealed record OutgoingProcessorDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
}

/// <summary>批次捕获的处理器代次，未执行阶段不能换成新版本。</summary>
public sealed record OutgoingProcessorDescriptor
{
    public required HandlerKey Key { get; init; }
    public required Guid Generation { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int MaxConcurrency { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>处理器上下文中的目标和调用身份只读，当前内容与原始草稿分别保留。</summary>
public sealed record OutgoingProcessingContext
{
    public required OutgoingBatch Original { get; init; }
    public required ImmutableArray<MessageDraft> Messages { get; init; }
    public required JsonElement Configuration { get; init; }
    public ConversationProfileSnapshot? Profile { get; init; }
}

/// <summary>处理器仅返回内容或显式过滤，不持有后续发送回调。</summary>
public sealed record OutgoingProcessingResult
{
    public required ImmutableArray<MessageDraft> Messages { get; init; }
    public bool Filtered { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
}

/// <summary>发送前内容处理由宿主顺序调用，插件不得重入发送服务。</summary>
public interface IOutgoingProcessor
{
    /// <summary>返回不可变变换结果，拆分内容继续交给后面的处理器。</summary>
    Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken);
}

/// <summary>出站处理器在插件初始化阶段注册，独立于消息监听器与生成工具。</summary>
public interface IOutgoingProcessorRegistry
{
    /// <summary>登记共享描述和实际执行器，初始化成功后统一发布。</summary>
    void Register(OutgoingProcessorDefinition definition, IOutgoingProcessor processor);
}

/// <summary>出站历史关联原始草稿、最终内容、处理来源及真实平台回执。</summary>
public sealed record OutgoingProcessingSnapshot
{
    public string? ProfileId { get; init; }
    public long? ConfigurationRevision { get; init; }
    public required OutgoingOptions Options { get; init; }
    public ImmutableArray<OutgoingProcessorDescriptor> SelectedProcessors { get; init; } = [];
    public ImmutableDictionary<string, JsonElement> PluginConfigurations { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>批次诊断同时保存捕获配置与成功应用的处理器，失败时仍能解释实际处理依据。</summary>
public sealed record OutgoingBatchSnapshot
{
    public required OutgoingBatch Original { get; init; }
    public ImmutableArray<MessageDraft> FinalMessages { get; init; } = [];
    public ImmutableArray<OutgoingProcessorDescriptor> Processors { get; init; } = [];
    public ToolApprovalResult? Approval { get; init; }
    public ToolApprovalRequest? ApprovalRequest { get; init; }
    public OutgoingProcessingSnapshot? Processing { get; init; }
    public required BatchSendResult Result { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>出站记录服务保存审计事实，不提供重放、重试或补发入口。</summary>
public interface IOutgoingHistory
{
    /// <summary>按完整会话范围读取批次，合并已保存的迟到单条回执。</summary>
    Task<OutgoingBatchSnapshot?> GetBatchAsync(MessageAddress address, Guid batchId, CancellationToken cancellationToken = default);
    /// <summary>读取指定任务最近的有限批次结果，不返回完整草稿或触发补发。</summary>
    Task<ImmutableArray<BatchSendResult>> GetTaskBatchesAsync(MessageAddress address, Guid taskId, int limit, CancellationToken cancellationToken = default);
}
