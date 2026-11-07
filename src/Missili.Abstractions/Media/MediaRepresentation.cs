using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>模型输入需要的媒体表示，派生文件继续由宿主媒体存储管理。</summary>
public sealed record MediaRepresentation
{
    public required Guid MediaId { get; init; }
    public required string MimeType { get; init; }
    public required string OriginalContentKey { get; init; }
}

/// <summary>格式准备通过适配器能力取得派生文件，不把重命名当作转码。</summary>
public interface IMediaRepresentationService
{
    /// <summary>返回候选可用的原件或 WAV 派生，失败明确说明。</summary>
    Task<MediaRepresentation> PrepareAudioAsync(Guid mediaId, ModelDefinition model, CancellationToken cancellationToken = default);
}
