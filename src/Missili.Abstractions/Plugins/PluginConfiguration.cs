using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>配置校验错误只保存共享字符串，不缓存插件私有异常或配置类型。</summary>
public sealed record PluginConfigurationError
{
    public required string Path { get; init; }
    public required string Message { get; init; }
}

/// <summary>一个插件只校验自己的命名空间，模型目录用于校验引用而不复制连接配置。</summary>
public sealed record PluginConfigurationContext
{
    public required string ProfileId { get; init; }
    public required JsonElement Configuration { get; init; }
    public required ImmutableDictionary<string, ModelDefinition> Models { get; init; }
    public required ImmutableDictionary<string, ModelProviderDefinition> Providers { get; init; }
}

/// <summary>插件校验不产生业务动作，只有全部校验成功才能发布新配置。</summary>
public interface IPluginConfigurationValidator
{
    /// <summary>检查字段、参数和引用，返回共享错误；私有解析结果仅在插件内使用。</summary>
    Task<ImmutableArray<PluginConfigurationError>> ValidateAsync(PluginConfigurationContext context, CancellationToken cancellationToken);
}

/// <summary>每个插件代次登记一个完整命名空间校验器。</summary>
public interface IPluginConfigurationRegistry
{
    /// <summary>初始化时登记，与工具及处理器共同发布和撤销。</summary>
    void Register(IPluginConfigurationValidator validator);
}
