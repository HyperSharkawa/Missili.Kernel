using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missili.Plugins.ReplyProcessing;

/// <summary>插件只解释自己的配置，用户显式排列替换与分段步骤。</summary>
internal sealed record ProcessingSettings
{
    public ImmutableArray<string> Purposes { get; init; } = ["reply"];
    public ImmutableArray<string> SourcePlugins { get; init; } = [];
    public ImmutableArray<string> SourceTools { get; init; } = [];
    public ImmutableArray<ProcessingStep> Steps { get; init; } = [];

    /// <summary>逐字段校验并解析正则，缺省配置不修改正文。</summary>
    public static ProcessingSettings Parse(JsonElement json)
    {
        CheckFields(json, ["purposes", "sourcePlugins", "sourceTools", "steps"]);
        ImmutableArray<ProcessingStep>.Builder steps = ImmutableArray.CreateBuilder<ProcessingStep>();
        if (json.TryGetProperty("steps", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException("steps 必须是数组。");
            foreach (JsonElement item in list.EnumerateArray())
            {
                string type = RequiredString(item, "type");
                if (type == "replace")
                {
                    CheckFields(item, ["type", "pattern", "replacement", "timeoutMilliseconds"]);
                    int timeout = Integer(item, "timeoutMilliseconds", 250);
                    steps.Add(new ProcessingStep { Pattern = new Regex(RequiredString(item, "pattern"), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(timeout)), Replacement = RequiredString(item, "replacement") });
                }
                else if (type == "split")
                {
                    CheckFields(item, ["type", "maximumTextElements"]);
                    steps.Add(new ProcessingStep { MaximumTextElements = Integer(item, "maximumTextElements", 800) });
                }
                else throw new ArgumentException("步骤 type 只能为 replace 或 split。");
            }
        }
        return new ProcessingSettings { Purposes = Strings(json, "purposes", ["reply"]), SourcePlugins = Strings(json, "sourcePlugins", []), SourceTools = Strings(json, "sourceTools", []), Steps = steps.ToImmutable() };
    }

    /// <summary>拒绝重复或未知字段，不允许参数拼写错误悄悄采用默认行为。</summary>
    private static void CheckFields(JsonElement json, string[] allowed)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("配置或步骤必须是对象。");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in json.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("重复或未知配置字段：" + property.Name);
    }

    /// <summary>模型与配置数字均不接受字符串转换。</summary>
    private static int Integer(JsonElement json, string key, int fallback)
    {
        if (!json.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 1) throw new ArgumentException(key + " 必须是正整数。");
        return number;
    }

    /// <summary>正则替换允许空字符串，但必须显式提供字符串值。</summary>
    private static string RequiredString(JsonElement json, string key)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(key + " 必须是字符串。");
        return value.GetString()!;
    }

    /// <summary>筛选使用完整稳定身份，空来源列表表示不按该来源过滤。</summary>
    private static ImmutableArray<string> Strings(JsonElement json, string key, ImmutableArray<string> fallback)
    {
        if (!json.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException(key + " 必须是字符串数组。");
        ImmutableArray<string>.Builder result = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new ArgumentException(key + " 不能含空值或非字符串。");
            result.Add(item.GetString()!);
        }
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Count) throw new ArgumentException(key + " 不能含重复身份。");
        return result.ToImmutable();
    }
}

/// <summary>已验证步骤只在插件内短暂使用，宿主只保存原始共享 JSON。</summary>
internal sealed record ProcessingStep
{
    public Regex? Pattern { get; init; }
    public string? Replacement { get; init; }
    public int MaximumTextElements { get; init; }
}
