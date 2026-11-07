using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>输入策略是会话行为，不修改归档原件。</summary>
public enum ModelMediaPolicy { OriginalPreferred, TextOnly, TranscriptionPreferred }

/// <summary>有限候选尝试与分阶段期限，持续生成没有总时限。</summary>
public sealed record ModelExecutionOptions
{
    public int Rounds { get; init; } = 2;
    public TimeSpan RoundDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeadersTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxOutputTokens { get; init; } = 4096;
    public bool CaptureSuccessfulDetails { get; init; }

    /// <summary>共享用途参数采用同一校验，模型型工具不另外引入隐式总时限。</summary>
    public void Validate()
    {
        if (Rounds < 1 || RoundDelay < TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero || HeadersTimeout <= TimeSpan.Zero || ReadIdleTimeout <= TimeSpan.Zero || MaxOutputTokens < 1)
            throw new ArgumentException("模型重试和期限参数无效。");
    }
}
