using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>配置发布与新插件代次激活共享短协调边界，避免校验后能力集合被替换。</summary>
public interface IPluginConfigurationRuntime
{
    /// <summary>取得发布资格；校验和原子发布完成后必须释放。</summary>
    Task<IPluginConfigurationUpdate> BeginUpdateAsync(CancellationToken cancellationToken);
}

/// <summary>一次发布资格不拥有插件生命周期，实际校验仍取得原代次调用资格。</summary>
public interface IPluginConfigurationUpdate : IDisposable
{
    /// <summary>逐一校验所有活动插件的各配置档；失败时不发布部分配置。</summary>
    Task ValidateAsync(ConversationConfiguration configuration, CancellationToken cancellationToken);
}
