using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>根任务创建时固定的树级预算，补充需求和子代理不能重置已用额度。</summary>
public sealed record AgentBudget
{
    public int DecisionsPerAgent { get; init; } = 32;
    public int ModelCalls { get; init; } = 128;
    public int ToolCalls { get; init; } = 256;
    public int TotalAgents { get; init; } = 16;
    public int WorkingAgents { get; init; } = 4;
    public int MaximumDepth { get; init; } = 2;

    /// <summary>预算约束实际调用次数，不用本地 token 估算代替配置。</summary>
    public void Validate()
    {
        if (DecisionsPerAgent < 1 || ModelCalls < 1 || ToolCalls < 1 || TotalAgents < 1 || WorkingAgents < 1 ||
            WorkingAgents > TotalAgents || MaximumDepth < 0) throw new ArgumentException("Agent 任务预算无效。");
    }
}

/// <summary>用户命名的子代理模板，具体任务由父代理另行传递。</summary>
public sealed record SubagentTemplate
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required string Instructions { get; init; }
    public required ImmutableArray<string> Models { get; init; }
    public bool Stream { get; init; } = true;
    public ImmutableArray<string> Tools { get; init; } = [AgentToolIds.FinishTurn];
    public ImmutableArray<string> Permissions { get; init; } = [];
}

/// <summary>独立配置档中的 Agent 行为，直接工具与允许委派的能力分别声明。</summary>
public sealed record AgentProfile
{
    public bool Enabled { get; init; }
    public ImmutableArray<string> Models { get; init; } = [];
    public bool Stream { get; init; } = true;
    public string Instructions { get; init; } = AgentDefaults.Instructions;
    public ImmutableArray<string> Tools { get; init; } = [AgentToolIds.FinishTurn];
    public ImmutableArray<string> DelegatedTools { get; init; } = [AgentToolIds.FinishTurn];
    public ImmutableArray<string> Permissions { get; init; } = [];
    public ImmutableArray<string> DelegatedPermissions { get; init; } = [];
    public ImmutableArray<string> Administrators { get; init; } = [];
    public ImmutableArray<SubagentTemplate> Templates { get; init; } = [];
    public string? DefaultTemplate { get; init; }
    public AgentOutputMode OutputMode { get; init; }
    public AgentBudget Budget { get; init; } = new();
    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>校验真实模型引用、模板和权限上限；工具暂未加载与非法配置分开处理。</summary>
    public void Validate(IReadOnlyDictionary<string, ModelDefinition> models, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        Budget.Validate();
        if (Instructions is null || !Enum.IsDefined(OutputMode) || GracefulStopTimeout <= TimeSpan.Zero || Templates.IsDefault)
            throw new ArgumentException("Agent 配置档参数无效。");
        ValidateIds(Tools, "主代理工具");
        ValidateIds(DelegatedTools, "委派工具");
        ValidateIds(Permissions, "主代理附加权限");
        ValidateIds(DelegatedPermissions, "委派附加权限");
        ValidateIds(Administrators, "管理员");
        ValidateModels(Models, Enabled, Stream, models, providers);
        HashSet<string> templates = new(StringComparer.Ordinal);
        foreach (SubagentTemplate template in Templates)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(template.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(template.Description);
            if (!templates.Add(template.Id) || template.Instructions is null) throw new ArgumentException("子代理模板名称重复或提示词无效。");
            ValidateIds(template.Tools, "子代理工具");
            ValidateIds(template.Permissions, "子代理附加权限");
            ValidateModels(template.Models, true, template.Stream, models, providers);
            if (template.Tools.Any(tool => !DelegatedTools.Contains(tool, StringComparer.Ordinal)))
                throw new ArgumentException("子代理模板超出配置档允许委派的工具范围。");
            if (template.Permissions.Any(permission => !DelegatedPermissions.Contains(permission, StringComparer.Ordinal)))
                throw new ArgumentException("子代理模板超出配置档允许委派的附加权限。");
        }
        bool canSpawn = Tools.Contains(AgentToolIds.Spawn, StringComparer.Ordinal) || Templates.Any(template => template.Tools.Contains(AgentToolIds.Spawn, StringComparer.Ordinal));
        if ((canSpawn || DefaultTemplate is not null) && (DefaultTemplate is null || !templates.Contains(DefaultTemplate)))
            throw new ArgumentException("允许创建子代理时必须选择有效的默认模板。");
    }

    /// <summary>工具与用户身份采用序数比较，不通过大小写或文本相似度合并权限。</summary>
    private static void ValidateIds(ImmutableArray<string> ids, string kind)
    {
        if (ids.IsDefault || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException(kind + "必须初始化且不得包含空值或重复标识。");
    }

    /// <summary>Agent 候选必须声明工具能力，不能将语音转写模型当作决策模型。</summary>
    private static void ValidateModels(ImmutableArray<string> candidates, bool required, bool stream,
        IReadOnlyDictionary<string, ModelDefinition> models, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        ValidateIds(candidates, "Agent 模型候选");
        if (required && candidates.IsEmpty) throw new ArgumentException("启用的 Agent 必须配置模型候选。");
        foreach (string id in candidates)
            if (!models.TryGetValue(id, out ModelDefinition? model) || !model.Tools || stream && !model.Streaming ||
                providers[model.ProviderId].Protocol == ModelProtocol.AudioTranscription)
                throw new ArgumentException("Agent 候选不存在或未声明对话工具能力：" + id);
    }
}
