using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Fixture.Plugin;

/// <summary>同一真实插件提供消息回调、草稿工具与出站处理，证明各执行通道的实际边界。</summary>
internal sealed class ExecutionProbe : IAgentToolHandler, IOutgoingProcessor
{
    /// <summary>处理器与工具共用一个代次，消息模式另登记持有回调槽的发送入口。</summary>
    public static void Register(IPluginContext context, bool messageHandler)
    {
        ExecutionProbe probe = new();
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
        context.Tools.Register(new AgentToolDefinition { Id = "probe.generate", Name = "probe_generate", Description = "产生合成草稿", OutputDescription = "草稿已生成",
            Parameters = schema.RootElement.Clone(), PermissionCategory = "test.generate" }, probe);
        context.OutgoingProcessors.Register(new OutgoingProcessorDefinition { Id = "probe", Name = "合成处理", Description = "为合成草稿添加处理标记" }, probe);
        if (messageHandler) context.Handlers.RegisterListener(MessageHooks.Received, "probe", "合成消息入口", "持有消息槽时等待同插件出站处理", 0,
            async (message, token) => { await context.Sender.SendAsync(OutgoingBatch.Single(message.OriginalMessage.Address, [SegmentValues.Text("handler")]), token); });
    }

    /// <summary>仅接受无参数调用，准备阶段没有发送或其他副作用。</summary>
    public Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(arguments.ValueKind == JsonValueKind.Object && !arguments.EnumerateObject().Any()
            ? new PreparedToolCall { Arguments = arguments.Clone() } : new PreparedToolCall { Error = "合成工具不接受参数。" });
    }

    /// <summary>返回后由宿主释放工具执行槽并接管草稿，插件没有直接发送能力调用。</summary>
    public Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolExecutionResult { State = ToolCallState.Succeeded, Drafts = [new MessageDraft { Segments = [SegmentValues.Text("tool")] }] });
    }

    /// <summary>成功结果证明同一插件的处理器已经实际执行，而非仅登记了描述。</summary>
    public Task<OutgoingProcessingResult> ProcessAsync(OutgoingProcessingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OutgoingProcessingResult { Messages = context.Messages.Select(message => message with
        { Segments = message.Segments.Add(SegmentValues.Text(":processed")) }).ToImmutableArray() });
    }
}
