using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>协议标识是数据边界，不根据模型名称推断协议或能力。</summary>
public enum ModelProtocol { ChatCompletions, Responses, Anthropic, AudioTranscription }

/// <summary>一次逻辑调用的确定性，记录失败仍可携带已经取得的结果。</summary>
public enum ModelCallState { Running, Succeeded, Failed, Cancelled, RecordingFailed, Interrupted }

/// <summary>通用内容块与协议专有续接块分别保存。</summary>
public enum ModelBlockKind { Text, Image, Audio, ToolCall, ToolResult, Native }

/// <summary>只引用归档媒体，二进制和 SDK 对象不进入长期请求记录。</summary>
public sealed record ModelBlock
{
    public ModelBlockKind Kind { get; init; }
    public string? Text { get; init; }
    public Guid? MediaId { get; init; }
    public string? MimeType { get; init; }
    public string? Name { get; init; }
    public string? ToolCallId { get; init; }
    public JsonElement? Data { get; init; }
    public bool Complete { get; init; } = true;
    /// <summary>构造正文内容，框架占位由调用方明确标识。</summary>
    public static ModelBlock TextBlock(string text) => new() { Kind = ModelBlockKind.Text, Text = text };
}

/// <summary>模型协议的真实轮次；群聊资料在正文中保留发言者身份。</summary>
public sealed record ModelMessage
{
    public required string Role { get; init; }
    public required ImmutableArray<ModelBlock> Content { get; init; }
    public ModelContinuation? Continuation { get; init; }
}

/// <summary>工具定义仅用于协议往返，本阶段没有自动执行器。</summary>
public sealed record ModelTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement Parameters { get; init; }
}

/// <summary>原生状态保留提供商和模型归属，禁止跨协议伪造续接。</summary>
public sealed record ModelContinuation
{
    public required ModelProtocol Protocol { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required JsonElement Items { get; init; }
}

/// <summary>未知用量保持空值，不用零表示服务端未提供的数据。</summary>
public sealed record ModelUsage
{
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
}

/// <summary>完整逻辑调用输入，在开始执行时捕获配置和内容所有权。</summary>
public sealed record ModelRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Purpose { get; init; }
    public required ConversationProfileSnapshot Profile { get; init; }
    public required ImmutableArray<string> Candidates { get; init; }
    public required ImmutableArray<ModelMessage> Messages { get; init; }
    public ImmutableArray<ModelTool> Tools { get; init; } = [];
    public MessageAddress? Conversation { get; init; }
    public long? TriggerMessageId { get; init; }
    public ConversationContextSnapshot? Context { get; init; }
    public bool Stream { get; init; } = true;
}

/// <summary>增量携带尝试身份；备用候选不能拼接到上一尝试的文字中。</summary>
public sealed record ModelUpdate
{
    public required Guid CallId { get; init; }
    public required Guid AttemptId { get; init; }
    public required string ModelId { get; init; }
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public JsonElement? Data { get; init; }
}

/// <summary>异步背压接收器，异常属于调用方错误而不是模型错误。</summary>
public interface IModelUpdateSink
{
    /// <summary>消费当前增量，完成前不继续向调用方推送。</summary>
    Task OnUpdateAsync(ModelUpdate update, CancellationToken cancellationToken);
}

/// <summary>一次实际候选尝试的纯数据摘要。</summary>
public sealed record ModelAttemptSummary
{
    public required Guid Id { get; init; }
    public required string ModelId { get; init; }
    public required string ProviderId { get; init; }
    public Guid? Generation { get; init; }
    public int Round { get; init; }
    public int Sequence { get; init; }
    public ModelCallState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
    public int? HttpStatus { get; init; }
    public string? RequestId { get; init; }
    public ModelUsage Usage { get; init; } = new();
    public bool HasPartialResult { get; init; }
    public string? DetailId { get; init; }
}

/// <summary>生成结果保留可用内容与完整性，取消不丢弃已接收片段。</summary>
public sealed record ModelResult
{
    public required Guid CallId { get; init; }
    public ModelCallState State { get; init; }
    public string? ModelId { get; init; }
    public string? FinishReason { get; init; }
    public ImmutableArray<ModelBlock> Content { get; init; } = [];
    public ModelUsage Usage { get; init; } = new();
    public ModelContinuation? Continuation { get; init; }
    public ImmutableArray<ModelAttemptSummary> Attempts { get; init; } = [];
    public string? Error { get; init; }
    public bool Complete { get; init; }
    public string Text => string.Concat(Content.Where(block => block.Kind == ModelBlockKind.Text).Select(block => block.Text));
}

/// <summary>插件统一模型入口，不暴露 SDK 或提供商客户端。</summary>
public interface IModelService
{
    /// <summary>执行有限候选策略；主动取消返回已取得的部分内容。</summary>
    Task<ModelResult> GenerateAsync(ModelRequest request, IModelUpdateSink? updates = null, CancellationToken cancellationToken = default);
}

/// <summary>协议请求已绑定一个候选与配置，不允许驱动内部重试。</summary>
public sealed record ModelAttemptRequest
{
    public required ModelRequest Request { get; init; }
    public required Guid AttemptId { get; init; }
    public required ModelDefinition Model { get; init; }
    public required ModelProviderDefinition Provider { get; init; }
}

/// <summary>协议执行结果，错误和不完整输出按值跨越可回收边界。</summary>
public sealed record ModelProtocolResult
{
    public ModelCallState State { get; init; }
    public ImmutableArray<ModelBlock> Content { get; init; } = [];
    public ModelUsage Usage { get; init; } = new();
    public JsonElement? Continuation { get; init; }
    public string? FinishReason { get; init; }
    public string? Error { get; init; }
    public string? ErrorKind { get; init; }
    public bool Complete { get; init; }
}

/// <summary>流式诊断和增量由宿主接收，不把插件专有对象放入缓存。</summary>
public interface IModelAttemptObserver
{
    /// <summary>保存不含凭据和媒体二进制的实际请求结构。</summary>
    Task RequestAsync(JsonElement request, CancellationToken cancellationToken);
    /// <summary>保存实际接收到的响应字节，未取得响应时不构造空成功响应。</summary>
    Task ResponseAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    /// <summary>保存响应头中的状态和服务端请求标识，不保存鉴权头。</summary>
    void ResponseHeaders(int status, string? requestId);
    /// <summary>转交当前尝试的增量并施加调用方背压。</summary>
    Task UpdateAsync(string kind, string? text, JsonElement? data, CancellationToken cancellationToken);
}

/// <summary>真实协议驱动随插件代次注册，具体 SDK 类型留在插件内。</summary>
public interface IModelProtocolDriver
{
    /// <summary>按同一协议序列化路径计算 JSON 请求字节数，不发送网络请求。</summary>
    Task<long> MeasureAsync(ModelAttemptRequest request, CancellationToken cancellationToken);
    /// <summary>执行一次请求，返回完整或部分结果；不执行候选循环。</summary>
    Task<ModelProtocolResult> ExecuteAsync(ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken cancellationToken);
}

/// <summary>注册协议能力，提供商实例通过配置目录绑定到协议。</summary>
public sealed record ModelProviderRegistration
{
    public required ModelProtocol Protocol { get; init; }
    public required IModelProtocolDriver Driver { get; init; }
}

/// <summary>注册在初始化期间暂存，和其他能力一起发布。</summary>
public interface IModelProviderRegistry
{
    /// <summary>登记当前插件实现的协议；重复协议拒绝发布。</summary>
    void Register(ModelProviderRegistration registration);
}

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
