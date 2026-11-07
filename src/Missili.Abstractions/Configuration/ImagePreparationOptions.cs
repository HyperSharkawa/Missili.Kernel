using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>图片预处理资源和编码限制，超限明确失败。</summary>
public sealed record ImagePreparationOptions
{
    public int Quality { get; init; } = 85;
    public int MaxWidth { get; init; } = 2048;
    public int MaxHeight { get; init; } = 2048;
    public double SliceRatio { get; init; } = 3;
    public int Overlap { get; init; } = 64;
    public long MaxRequestBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxImagesPerRequest { get; init; } = 20;
    public int MaxSlices { get; init; } = 128;
    public double FramesPerSecond { get; init; } = 2;
    public int MaxSampleFrames { get; init; } = 8;
    public long MaxSourceBytes { get; init; } = 128 * 1024 * 1024;
    public long MaxDecodedPixels { get; init; } = 64_000_000;
    public int MaxSourceFrames { get; init; } = 512;

    /// <summary>文件配置和直接调用共享同一校验，切片步长必须严格为正。</summary>
    public void Validate()
    {
        if (Quality is < 1 or > 100 || MaxWidth < 1 || MaxHeight < 1 || !double.IsFinite(SliceRatio) || SliceRatio <= 1 ||
            Overlap < 0 || Overlap >= Math.Min(MaxWidth, MaxHeight) || MaxRequestBytes < 1 || MaxImagesPerRequest < 1 || MaxSlices < 1 ||
            !double.IsFinite(FramesPerSecond) || FramesPerSecond <= 0 || MaxSampleFrames < 1 || MaxSourceBytes < 1 || MaxDecodedPixels < 1 || MaxSourceFrames < 1)
            throw new ArgumentException("图片预处理参数无效。");
    }
}
