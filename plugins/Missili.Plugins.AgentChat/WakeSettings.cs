using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Missili.Abstractions;

namespace Missili.Plugins.AgentChat;

/// <summary>入口私有配置只在加载代次内存活，宿主保留的仍是共享 JSON。</summary>
internal sealed record WakeSettings
{
    private static readonly JsonSerializerOptions AddressOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public ImmutableArray<string> WakePrefixes { get; init; } = ["/agent"];
    public bool Mentions { get; init; } = true;
    public ImmutableArray<string> Keywords { get; init; } = [];
    public bool ProbabilityEnabled { get; init; }
    public double Probability { get; init; }
    public ImmutableArray<MessageAddress> AllowedConversations { get; init; } = [];

    /// <summary>完整地址避免不同账号、平台或适配器共享同一群号时越界。</summary>
    public bool Allows(MessageAddress address) => AllowedConversations.IsEmpty || AllowedConversations.Contains(address);

    /// <summary>严格读取字面规则，不静默修剪、归一化、转换类型或接受旧配置字段。</summary>
    public static WakeSettings Read(JsonElement json)
    {
        Fields(json, ["wakePrefixes", "mentions", "keywords", "probability", "allowedConversations"]);
        bool probabilityEnabled = false;
        double probability = 0;
        if (json.TryGetProperty("probability", out JsonElement chance))
        {
            Fields(chance, ["enabled", "value"]);
            probabilityEnabled = chance.TryGetProperty("enabled", out JsonElement enabled) && enabled.GetBoolean();
            if (chance.TryGetProperty("value", out JsonElement value))
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
                    throw new ArgumentException("概率必须为 0 到 1 之间的有限数值。");
            }
        }
        ImmutableArray<MessageAddress> addresses = [];
        if (json.TryGetProperty("allowedConversations", out JsonElement allowed))
        {
            if (allowed.ValueKind != JsonValueKind.Array) throw new ArgumentException("会话白名单必须是数组。");
            foreach (JsonElement address in allowed.EnumerateArray())
                Fields(address, ["adapterId", "platformId", "botAccountId", "conversationKind", "conversationId"]);
            addresses = JsonSerializer.Deserialize<ImmutableArray<MessageAddress>>(allowed, AddressOptions);
            if (addresses.IsDefault || addresses.Distinct().Count() != addresses.Length) throw new ArgumentException("会话白名单无效或重复。");
            foreach (MessageAddress address in addresses) address.Validate();
        }
        return new WakeSettings
        {
            WakePrefixes = Words(json, "wakePrefixes", ["/agent"], StringComparer.Ordinal),
            Mentions = !json.TryGetProperty("mentions", out JsonElement mentions) || mentions.GetBoolean(),
            Keywords = Words(json, "keywords", [], StringComparer.OrdinalIgnoreCase),
            ProbabilityEnabled = probabilityEnabled, Probability = probability, AllowedConversations = addresses
        };
    }

    /// <summary>数组逐项保持原值，重复判定与实际匹配的大小写规则相同。</summary>
    private static ImmutableArray<string> Words(JsonElement json, string name, ImmutableArray<string> defaults, StringComparer comparer)
    {
        if (!json.TryGetProperty(name, out JsonElement value)) return defaults;
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException(name + " 必须为字符串数组。");
        ImmutableArray<string>.Builder words = ImmutableArray.CreateBuilder<string>();
        HashSet<string> seen = new(comparer);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || !seen.Add(item.GetString()!))
                throw new ArgumentException(name + " 不能包含空白、非字符串或重复项。");
            words.Add(item.GetString()!);
        }
        return words.ToImmutable();
    }

    /// <summary>未知字段与重复字段都拒绝，避免拼写错误或后值覆盖改变配置意图。</summary>
    private static void Fields(JsonElement json, string[] allowed)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("入口配置必须是对象。");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in json.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException("入口配置字段重复或未声明：" + property.Name);
    }
}
