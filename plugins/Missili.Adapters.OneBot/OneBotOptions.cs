using System.Globalization;
using System.Text.Json.Serialization;

namespace Missili.Adapters.OneBot;

/// <summary>连接建立方向，双方共用相同的 OneBot 编解码和请求关联。</summary>
public enum ConnectionDirection { Forward, Reverse }
/// <summary>单条合并连接或两条角色分离连接。</summary>
public enum ConnectionLayout { Universal, Split }
/// <summary>明确选择 Token 或无鉴权，缺失配置不会自动关闭鉴权。</summary>
public enum AuthenticationMode { Token, None }
/// <summary>标准解析与有样本依据的实现兼容分离。</summary>
public enum CompatibilityProfile { Standard, SnowLuma }

/// <summary>一个官方插件可登记多个独立适配器实例。</summary>
public sealed class PluginOptions
{
    public OneBotOptions[] Instances { get; set; } = [];
}

/// <summary>远端媒体目录到本地只读目录的显式映射，默认不配置任何映射。</summary>
public sealed class MediaPathMapping
{
    public required string RemoteRoot { get; set; }
    public required string LocalRoot { get; set; }
}

/// <summary>OneBot 实例配置，默认容量只是资源限制，不表示性能承诺。</summary>
public sealed class OneBotOptions
{
    public required string Id { get; set; }
    public required string BotAccountId { get; set; }
    public ConnectionDirection Direction { get; set; }
    public ConnectionLayout Layout { get; set; }
    public string? Url { get; set; }
    public string? ApiUrl { get; set; }
    public string? EventUrl { get; set; }
    public AuthenticationMode Authentication { get; set; } = AuthenticationMode.Token;
    public string? Token { get; set; }
    public string? TokenEnvironmentVariable { get; set; }
    public CompatibilityProfile CompatibilityProfile { get; set; }
    public MediaPathMapping[] PathMappings { get; set; } = [];
    public string RecordFormat { get; set; } = "wav";
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdentityTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectMinimum { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaximum { get; set; } = TimeSpan.FromSeconds(30);
    public double ReconnectJitter { get; set; } = 0.2;
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxPendingRequests { get; set; } = 32;
    public int SendCapacity { get; set; } = 128;
    public int EventCapacity { get; set; } = 256;
    public long InboundByteBudget { get; set; } = 64 * 1024 * 1024;
    public int MaxMessageBytes { get; set; } = 64 * 1024 * 1024;
    public int MemoryThreshold { get; set; } = 1024 * 1024;
    public long MaxOutgoingMediaBytes { get; set; } = 32 * 1024 * 1024;
    public int LateReceiptCapacity { get; set; } = 256;
    public TimeSpan LateReceiptRetention { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxForwardDepth { get; set; } = 5;
    public int MaxForwardNodes { get; set; } = 500;
    public int MaxForwardSegments { get; set; } = 5000;
    public int MaxForwardRequests { get; set; } = 64;
    public int MaxForwardBytes { get; set; } = 16 * 1024 * 1024;
    public int JsonMaxDepth { get; set; } = 64;

    /// <summary>启动前验证组合；仅读取明确命名的环境变量，不把凭据放入诊断。</summary>
    public string? ValidateAndGetToken()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        if (Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))) throw new ArgumentException("适配器 ID 格式无效。");
        if (!long.TryParse(BotAccountId, NumberStyles.None, CultureInfo.InvariantCulture, out long identity) || identity <= 0) throw new ArgumentException("Bot 账号必须为有效整数标识。");
        if (!Enum.IsDefined(Direction) || !Enum.IsDefined(Layout) || !Enum.IsDefined(Authentication) || !Enum.IsDefined(CompatibilityProfile)) throw new ArgumentException("连接枚举配置无效。");
        if (Direction == ConnectionDirection.Forward)
        {
            if (Layout == ConnectionLayout.Universal)
            { ValidateUrl(Url); if (ApiUrl is not null || EventUrl is not null) throw new ArgumentException("合并布局不能同时配置分离地址。"); }
            else { ValidateUrl(ApiUrl); ValidateUrl(EventUrl); if (Url is not null) throw new ArgumentException("分离布局不能同时配置合并地址。"); }
        }
        else if (Url is not null || ApiUrl is not null || EventUrl is not null) throw new ArgumentException("反向实例不配置主动连接地址。");
        int[] capacities = [MaxPendingRequests, SendCapacity, EventCapacity, MaxMessageBytes, MemoryThreshold, LateReceiptCapacity,
            MaxForwardDepth, MaxForwardNodes, MaxForwardSegments, MaxForwardRequests, MaxForwardBytes, JsonMaxDepth];
        if (capacities.Any(value => value <= 0) || InboundByteBudget < MaxMessageBytes || MemoryThreshold > MaxMessageBytes || MaxOutgoingMediaBytes <= 0)
            throw new ArgumentException("容量、载荷预算或暂存阈值无效。");
        TimeSpan[] deadlines = [ConnectTimeout, IdentityTimeout, RequestTimeout, CloseTimeout, ReconnectMinimum, ReconnectMaximum, KeepAliveInterval, KeepAliveTimeout, LateReceiptRetention];
        if (deadlines.Any(value => value <= TimeSpan.Zero) || ReconnectMinimum > ReconnectMaximum || !double.IsFinite(ReconnectJitter) || ReconnectJitter is < 0 or > 1)
            throw new ArgumentException("连接期限或退避配置无效。");
        if (RecordFormat is not ("mp3" or "amr" or "wma" or "m4a" or "spx" or "ogg" or "wav" or "flac")) throw new ArgumentException("语音转换格式不在标准支持范围内。");
        foreach (MediaPathMapping mapping in PathMappings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.RemoteRoot);
            if (!Path.IsPathFullyQualified(mapping.LocalRoot)) throw new ArgumentException("媒体本地映射必须为明确绝对目录。");
        }
        if (Authentication == AuthenticationMode.None)
        { if (Token is not null || TokenEnvironmentVariable is not null) throw new ArgumentException("无鉴权模式不能同时提供 Token。"); return null; }
        if ((Token is null) == (TokenEnvironmentVariable is null)) throw new ArgumentException("必须且只能配置一个 Token 来源。");
        string? token = Token ?? Environment.GetEnvironmentVariable(TokenEnvironmentVariable!);
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("配置的 Token 来源为空。");
        return token;
    }

    /// <summary>只接受完整 WS 地址，保留正常 TLS 验证，不推导用户未提供的端点。</summary>
    private static void ValidateUrl(string? value)
    { if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("ws" or "wss")) throw new ArgumentException("连接地址必须为完整 ws 或 wss URL。"); }
}

/// <summary>插件私有配置使用插件自己的元数据，避免全局反射缓存阻止卸载。</summary>
[JsonSerializable(typeof(PluginOptions))]
[JsonSerializable(typeof(ExpansionCheckpoint))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class OneBotJsonContext : JsonSerializerContext { }
