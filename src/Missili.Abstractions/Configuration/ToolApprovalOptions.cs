using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>政策选择要么使用预设加补充，要么使用完整自定义正文。</summary>
public sealed record ToolApprovalPolicy
{
    public string? TemplateId { get; init; }
    public string? AdditionalText { get; init; }
    public string? CustomText { get; init; }

    /// <summary>两种政策模式互斥，不隐式叠加自定义政策和全局模板。</summary>
    public void Validate()
    {
        if (CustomText is not null)
        {
            if (string.IsNullOrWhiteSpace(CustomText) || TemplateId is not null || AdditionalText is not null)
                throw new ArgumentException("完整自定义审批政策不能同时选择模板或补充文本。");
        }
        else if (TemplateId != "codex-default") throw new ArgumentException("审批政策模板不存在。");
    }
}

/// <summary>逐工具只覆盖明确提供的选项，政策整体替换而不是逐段继承。</summary>
public sealed record ToolApprovalOverride
{
    public bool? Enabled { get; init; }
    public bool? Stream { get; init; }
    public ImmutableArray<string>? Models { get; init; }
    public ToolApprovalPolicy? Policy { get; init; }
    public ModelExecutionOptions? Execution { get; init; }
}

/// <summary>会话统一审批设置，稳定工具 ID 与最终 message.send 动作使用同一配置入口。</summary>
public sealed record ToolApprovalOptions
{
    public bool Enabled { get; init; }
    public bool Stream { get; init; } = true;
    public ImmutableArray<string> Models { get; init; } = [];
    public ToolApprovalPolicy Policy { get; init; } = new() { TemplateId = "codex-default" };
    public ModelExecutionOptions? Execution { get; init; }
    public ImmutableDictionary<string, ToolApprovalOverride> Tools { get; init; } = ImmutableDictionary<string, ToolApprovalOverride>.Empty;

    /// <summary>按一个稳定动作生成完整有效配置，不修改原快照。</summary>
    public EffectiveToolApproval Resolve(string toolId, ModelExecutionOptions defaultExecution)
    {
        Tools.TryGetValue(toolId, out ToolApprovalOverride? selected);
        return new EffectiveToolApproval
        {
            Enabled = selected?.Enabled ?? Enabled,
            Stream = selected?.Stream ?? Stream,
            Models = selected?.Models ?? Models,
            Policy = selected?.Policy ?? Policy,
            Execution = selected?.Execution ?? Execution ?? defaultExecution
        };
    }

    /// <summary>在发布配置前核对所有启用审批的模型用途，关闭项不要求虚构模型候选。</summary>
    public void Validate(ModelExecutionOptions defaultExecution, IReadOnlyDictionary<string, ModelDefinition> models, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        Policy.Validate();
        if (Models.IsDefault || Tools is null) throw new ArgumentException("审批配置集合未初始化。");
        ValidateEffective(Resolve("", defaultExecution), models, providers);
        foreach (KeyValuePair<string, ToolApprovalOverride> item in Tools)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            item.Value.Policy?.Validate();
            ValidateEffective(Resolve(item.Key, defaultExecution), models, providers);
        }
    }

    /// <summary>审批只需要对话能力，不能强制审批者具备它不应使用的工具执行能力。</summary>
    private static void ValidateEffective(EffectiveToolApproval value, IReadOnlyDictionary<string, ModelDefinition> models, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        if (value.Models.IsDefault || value.Models.Any(string.IsNullOrWhiteSpace) || value.Models.Distinct(StringComparer.Ordinal).Count() != value.Models.Length ||
            value.Enabled && value.Models.IsEmpty) throw new ArgumentException("审批模型候选无效。");
        foreach (string id in value.Models)
            if (!models.TryGetValue(id, out ModelDefinition? model) || providers[model.ProviderId].Protocol == ModelProtocol.AudioTranscription || value.Stream && !model.Streaming)
                throw new ArgumentException("审批候选不存在或不是对话模型：" + id);
        value.Execution.Validate();
    }
}

/// <summary>本次具体动作捕获的审批配置，与无关配置修订分离。</summary>
public sealed record EffectiveToolApproval
{
    public required bool Enabled { get; init; }
    public required bool Stream { get; init; }
    public required ImmutableArray<string> Models { get; init; }
    public required ToolApprovalPolicy Policy { get; init; }
    public required ModelExecutionOptions Execution { get; init; }
}
