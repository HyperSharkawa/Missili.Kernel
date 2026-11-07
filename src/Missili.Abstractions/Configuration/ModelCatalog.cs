using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>提供商连接配置只保存凭据引用，不发现其他环境中的隐式凭据。</summary>
public sealed record ModelProviderDefinition
{
    public required string Id { get; init; }
    public required ModelProtocol Protocol { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string ApiKeyEnvironmentVariable { get; init; }
    public int MaxConcurrency { get; init; } = 4;
}

/// <summary>能力由用户声明，不通过模型名称启发式猜测。</summary>
public sealed record ModelDefinition
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public bool Images { get; init; }
    public bool Audio { get; init; }
    public bool Tools { get; init; }
    public bool Streaming { get; init; } = true;
    public int MaxImages { get; init; } = 20;
    public long MaxRequestBytes { get; init; } = 8 * 1024 * 1024;
    public ImmutableArray<string> AudioFormats { get; init; } = ["wav", "mp3", "m4a", "flac", "ogg", "webm"];
    public JsonElement? Parameters { get; init; }
}

/// <summary>查询纯值目录，不暴露凭据内容。</summary>
public interface IModelCatalog
{
    /// <summary>取得当前有效模型目录。</summary>
    ImmutableArray<ModelDefinition> GetModels();
    /// <summary>取得当前有效提供商连接声明。</summary>
    ImmutableArray<ModelProviderDefinition> GetProviders();
}
