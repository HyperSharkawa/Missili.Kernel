using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Missili.Abstractions;

namespace Missili.Tools.Reply;

/// <summary>回复模型与提示词属于插件命名空间，连接仍引用宿主唯一目录。</summary>
internal sealed class ReplySettings
{
    // 源生成反序列化通过属性赋值保留缺省初始化值；此 DTO 只在插件内部解析，不发布为共享快照。
    public ImmutableArray<string> Models { get; set; } = [];
    public string SystemPrompt { get; set; } = "依据本次要求、明确事实与所选成果生成对用户的完整回复。保持来源和完整性，不能把部分成果当作完整结论，不执行工具。";
    public bool Stream { get; set; } = true;
    public ModelExecutionOptions? Execution { get; set; }

    /// <summary>私有 DTO 通过插件源生成元数据解析，不进入宿主反射构造器缓存。</summary>
    public static ReplySettings Parse(JsonElement json, IReadOnlyDictionary<string, ModelDefinition> models, IReadOnlyDictionary<string, ModelProviderDefinition> providers)
    {
        Unique(json);
        ReplySettings settings = JsonSerializer.Deserialize(json, ReplyJsonContext.Default.ReplySettings) ?? throw new ArgumentException("回复配置不能为空。");
        if (settings.Models.IsDefault || settings.Models.Any(string.IsNullOrWhiteSpace) || settings.Models.Distinct(StringComparer.Ordinal).Count() != settings.Models.Length || settings.SystemPrompt is null)
            throw new ArgumentException("回复模型候选或提示词无效。");
        foreach (string id in settings.Models)
            if (!models.TryGetValue(id, out ModelDefinition? model) || providers[model.ProviderId].Protocol == ModelProtocol.AudioTranscription || settings.Stream && !model.Streaming)
                throw new ArgumentException("回复候选不存在或不支持指定调用方式：" + id);
        settings.Execution?.Validate();
        return settings;
    }

    /// <summary>重复字段不能通过后值覆盖改变用户原配置含义。</summary>
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        { if (!names.Add(property.Name)) throw new ArgumentException("回复配置字段重复：" + property.Name); Unique(property.Value); }
    }
}

/// <summary>私有配置的类型元数据与插件一起回收，不登记到宿主解析器链。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReplySettings))]
internal partial class ReplyJsonContext : JsonSerializerContext { }
