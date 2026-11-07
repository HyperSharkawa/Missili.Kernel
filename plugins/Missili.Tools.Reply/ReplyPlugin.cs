using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Tools.Reply;

/// <summary>官方回复工具仅生成共享草稿，后处理、审批及平台传输全部由宿主负责。</summary>
public sealed class ReplyPlugin : IPlugin, IAgentToolHandler, IPluginConfigurationValidator
{
    private const string PluginId = "missili.tools.reply";
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new Dictionary<string, string>());

    /// <summary>登记生成与配置校验能力，不监听消息或自行接管发送。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument schema = JsonDocument.Parse("""
            {"type":"object","properties":{
                "instructions":{"type":"string","description":"本次回复的要求"},
                "facts":{"type":"array","items":{"type":"string"},"description":"需要明确表达的事实"},
                "result_references":{"type":"array","items":{"type":"object","properties":{"agent_id":{"type":"string"},"entry_sequence":{"type":"integer","minimum":1}},"required":["agent_id","entry_sequence"],"additionalProperties":false}}
            },"required":["instructions","facts"],"additionalProperties":false}
            """);
        context.ConfigurationValidators.Register(this);
        context.Tools.Register(new AgentToolDefinition
        {
            Id = "reply.generate",
            Name = "reply",
            Description = "根据要求、事实与明确选取的同任务成果生成回复草稿，由宿主统一处理和发送。",
            Parameters = schema.RootElement,
            OutputDescription = "生成状态、模型调用身份，以及宿主后续处理和发送结果。",
            ExecutionTimeout = null,
            PermissionCategory = "message.compose"
        }, this);
        return Task.CompletedTask;
    }

    /// <summary>生成请求随宿主调用资格结束，无额外后台任务。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>安装插件不要求所有配置档都使用它，配置了候选时必须全部有效。</summary>
    public Task<ImmutableArray<PluginConfigurationError>> ValidateAsync(PluginConfigurationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = ReplySettings.Parse(context.Configuration, context.Models, context.Providers); return Task.FromResult(ImmutableArray<PluginConfigurationError>.Empty); }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        { return Task.FromResult(ImmutableArray.Create(new PluginConfigurationError { Path = "$", Message = exception.Message })); }
    }

    /// <summary>校验参数并解析选中的可展示成果；不调用模型、不处理正文、不发送。</summary>
    public async Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            Fields(arguments, ["instructions", "facts", "result_references"]);
            if (!arguments.TryGetProperty("instructions", out JsonElement instructions) || instructions.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(instructions.GetString()) ||
                !arguments.TryGetProperty("facts", out JsonElement facts) || facts.ValueKind != JsonValueKind.Array || facts.EnumerateArray().Any(fact => fact.ValueKind != JsonValueKind.String))
                return new PreparedToolCall { Error = "instructions 必须是非空字符串，facts 必须是字符串数组。" };
            ReplySettings settings = Settings(context.Profile);
            if (settings.Models.IsEmpty) return new PreparedToolCall { Error = "当前配置档没有配置回复模型候选。" };
            List<Dictionary<string, object?>> materials = [];
            if (arguments.TryGetProperty("result_references", out JsonElement references))
            {
                if (references.ValueKind != JsonValueKind.Array) return new PreparedToolCall { Error = "result_references 必须是数组。" };
                foreach (JsonElement reference in references.EnumerateArray())
                {
                    Fields(reference, ["agent_id", "entry_sequence"]);
                    if (!reference.TryGetProperty("agent_id", out JsonElement id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out Guid agent) ||
                        !reference.TryGetProperty("entry_sequence", out JsonElement sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out long position) || position < 1 || context.Origin.TaskId is not Guid task)
                        return new PreparedToolCall { Error = "成果引用必须包含同任务代理身份及正整数记录序号。" };
                    AgentEntryPage page = await context.AgentHistory.ReadEntriesAsync(context.Conversation, task, agent, position - 1, 1, cancellationToken).ConfigureAwait(false);
                    AgentEntry? entry = page.Entries.SingleOrDefault();
                    if (entry is null || entry.Sequence != position || entry.Kind != AgentEntryKind.Result) return new PreparedToolCall { Error = "所选同任务成果不存在或不是可展示的成果记录。" };
                    AgentResult result = JsonSerializer.Deserialize<AgentResult>(entry.Data)!;
                    materials.Add(new Dictionary<string, object?>
                    {
                        ["agent_id"] = agent,
                        ["entry_sequence"] = position,
                        ["complete"] = result.Complete,
                        ["text"] = string.Concat(result.Content.Where(block => block.Kind == ModelBlockKind.Text).Select(block => block.Text)),
                        ["unfinished"] = result.Unfinished,
                        ["error"] = result.Error
                    });
                }
            }
            return new PreparedToolCall
            {
                Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["instructions"] = instructions.GetString(), ["facts"] = facts.Clone(), ["materials"] = materials }),
                ExecutionDescription = "使用配置档中的回复模型生成草稿；草稿交给宿主进行后处理和独立发送审批。"
            };
        }
        catch (ArgumentException exception) { return new PreparedToolCall { Error = exception.Message }; }
    }

    /// <summary>仅完整且已记录的普通文本能成为草稿，部分输出保留在工具记录中。</summary>
    public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        ReplySettings settings = Settings(context.Profile);
        ModelResult model = await context.Models.GenerateAsync(new ModelRequest
        {
            Purpose = "reply",
            Profile = context.Profile with { Profile = context.Profile.Profile with { Execution = settings.Execution ?? context.Profile.Profile.Execution } },
            Candidates = settings.Models,
            Stream = settings.Stream,
            Conversation = context.Conversation,
            Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock(prepared.Arguments!.Value.GetRawText())] }], settings.SystemPrompt)
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        bool sendable = model.State == ModelCallState.Succeeded && model.Complete && !string.IsNullOrWhiteSpace(model.Text) && !model.Content.Any(block => block.Kind == ModelBlockKind.ToolCall);
        return new ToolExecutionResult
        {
            State = sendable ? ToolCallState.Succeeded : model.State == ModelCallState.Cancelled ? ToolCallState.CancelledDuringExecution : ToolCallState.Failed,
            Complete = model.Complete,
            Text = model.Text,
            Error = sendable ? null : model.Error ?? "回复模型没有返回可发送的完整普通文本。",
            ErrorKind = sendable ? null : "Model",
            Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["model_call_id"] = model.CallId, ["text"] = model.Text }),
            ModelData = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["model_call_id"] = model.CallId, ["generated"] = sendable, ["characters"] = model.Text.Length }),
            Drafts = sendable ? [new MessageDraft { Segments = [SegmentValues.Text(model.Text)] }] : []
        };
    }

    /// <summary>工具执行固定当前上下文配置，不读取正在变化的宿主目录。</summary>
    private static ReplySettings Settings(ConversationProfileSnapshot profile)
        => ReplySettings.Parse(profile.Profile.Plugins.TryGetValue(PluginId, out JsonElement configuration) ? configuration : Empty, profile.Models, profile.Providers);

    /// <summary>真实参数不允许未知字段或重复字段，模型不能覆盖连接与系统提示词。</summary>
    private static void Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("回复参数必须是对象。");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("回复参数包含重复或未知字段：" + property.Name);
    }
}
