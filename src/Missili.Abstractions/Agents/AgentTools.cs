using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>工具实际结果与模型完成状态分开，未知副作用不能被当作未执行。</summary>
public enum ToolCallState { Pending, Preparing, Reviewing, Running, Completing, Succeeded, Failed, Rejected, ApprovalDenied, ApprovalFailed, Unavailable, CancelledBeforeExecution, CancelledDuringExecution, Unknown, RecordingFailed, Interrupted }

/// <summary>对外效果是否已开始及是否确定，用于停止、重启和人工排障。</summary>
public enum ToolEffectState { NotStarted, Started, Confirmed, Unknown }

/// <summary>工具稳定描述由宿主复制，模型目录不持有插件执行器。</summary>
public sealed record AgentToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement Parameters { get; init; }
    public required string OutputDescription { get; init; }
    public required string PermissionCategory { get; init; }
    public bool HasSideEffects { get; init; }
    public TimeSpan? ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public bool IsControl { get; init; }
    public ImmutableArray<string> DeclaredPermissions { get; init; } = [];
}

/// <summary>一次模型调用捕获的工具目录项，执行时必须匹配原代次。</summary>
public sealed record AgentToolDescriptor
{
    public required AgentToolDefinition Definition { get; init; }
    public required string PluginId { get; init; }
    public required Guid Generation { get; init; }
    public int MaxConcurrency { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>无副作用准备结果固定实际参数与环境说明，校验失败不会进入执行阶段。</summary>
public sealed record PreparedToolCall
{
    public JsonElement? Arguments { get; init; }
    public string? ExecutionDescription { get; init; }
    public string? Error { get; init; }
    public string? ErrorPath { get; init; }
    public ImmutableArray<string> RequiredPermissions { get; init; } = [];
    public bool Valid => Arguments is { ValueKind: JsonValueKind.Object } && Error is null;

    /// <summary>复制实际参数的 JSON 所有权，不保留插件的文档或私有参数对象。</summary>
    public PreparedToolCall Freeze() => this with { Arguments = Arguments?.Clone() };
}

/// <summary>插件执行返回共享数据与可选草稿，后续动作由宿主接管，不返回回调。</summary>
public sealed record ToolExecutionResult
{
    public required ToolCallState State { get; init; }
    public JsonElement? Data { get; init; }
    /// <summary>可选的有界模型投影，替代模型可见的完整 Data/Text；原件仍保存供工具记录读取。</summary>
    public JsonElement? ModelData { get; init; }
    public string? Text { get; init; }
    public bool Complete { get; init; } = true;
    public ToolEffectState Effect { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
    public ImmutableArray<MessageDraft> Drafts { get; init; } = [];

    /// <summary>插件结果转换为宿主长期可持有的数据，不保留外部 JSON 文档。</summary>
    public ToolExecutionResult Freeze() => this with
    { Data = Data?.Clone(), ModelData = ModelData?.Clone(), Drafts = Drafts.Select(draft => draft.Freeze()).ToImmutableArray() };
}

/// <summary>工具只获得本次调用所需的共享服务，模型服务由宿主绑定任务预算与归属。</summary>
public sealed record AgentToolContext
{
    public required OperationOrigin Origin { get; init; }
    public required MessageAddress Conversation { get; init; }
    public required ConversationProfileSnapshot Profile { get; init; }
    public required IModelService Models { get; init; }
    public required IMessageHistory History { get; init; }
    public required IAgentHistory AgentHistory { get; init; }
    public required IAgentToolHistory ToolHistory { get; init; }
    public required IHistorySearch Search { get; init; }
    public required IConversationAssetAccess Assets { get; init; }
    public required IStructuredContentStore Contents { get; init; }
    public required IMediaStore Media { get; init; }
    public required IMediaTranscriptionService Transcriptions { get; init; }
    public required TimeProvider Clock { get; init; }
    public int MaximumResultBytes { get; init; } = 64 * 1024;
}

/// <summary>工具执行器只在所属加载代次内存活，准备和执行之间传递共享冻结数据。</summary>
public interface IAgentToolHandler
{
    /// <summary>严格校验并准备实际参数，不发起业务副作用。</summary>
    Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken);
    /// <summary>对已经批准的实际参数执行一次，不自行重试或替换参数。</summary>
    Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken);
}

/// <summary>插件初始化登记工具，全部能力随同一个加载代次激活。</summary>
public interface IAgentToolRegistry
{
    /// <summary>登记稳定描述及执行器，冲突在发布前拒绝。</summary>
    void Register(AgentToolDefinition definition, IAgentToolHandler handler);
}
