using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>宿主模型模块容量；关闭时不启动扫描或接受模型业务。</summary>
public sealed record ModelOptions
{
    public bool Enabled { get; init; }
    public int NetworkConcurrency { get; init; } = 8;
    public int QueueCapacity { get; init; } = 128;
    public int TranscriptionConcurrency { get; init; } = 2;
    public int TranscriptionCapacity { get; init; } = 128;
    public int ImageConcurrency { get; init; } = 1;
    /// <summary>在启动工作循环之前拒绝非法容量。</summary>
    public void Validate()
    {
        if (NetworkConcurrency < 1 || QueueCapacity < 1 || TranscriptionConcurrency < 1 ||
            TranscriptionCapacity < TranscriptionConcurrency || ImageConcurrency < 1)
            throw new ArgumentException("模型模块容量必须为正数，转述队列不能小于并发数。");
    }
}
