using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>配置档拥有出站处理顺序，明确列出的处理器不可用时不能跳过。</summary>
public sealed record OutgoingOptions
{
    public ImmutableArray<HandlerKey> Processors { get; init; } = [];
    public int MaximumMessages { get; init; } = 16;
    public int MaximumJsonBytes { get; init; } = 1024 * 1024;
    public TimeSpan ProcessorTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>顺序只接受唯一的完整处理器身份，空列表表示没有内容变换。</summary>
    public void Validate()
    {
        if (Processors.IsDefault || Processors.Distinct().Count() != Processors.Length || MaximumMessages < 1 || MaximumJsonBytes < 1 || ProcessorTimeout <= TimeSpan.Zero ||
            Processors.Any(item => string.IsNullOrWhiteSpace(item.PluginId) || string.IsNullOrWhiteSpace(item.HandlerId)))
            throw new ArgumentException("出站处理顺序、容量或期限无效。");
    }
}
