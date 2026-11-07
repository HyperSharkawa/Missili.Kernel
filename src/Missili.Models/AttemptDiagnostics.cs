using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Models;

/// <summary>一次尝试拥有的磁盘详情，原始响应流式落盘，不积压无界内存。</summary>
internal sealed class AttemptDiagnostics : IModelAttemptObserver, IAsyncDisposable
{
    private readonly ModelAttemptRequest _attempt;
    private readonly IModelUpdateSink? _sink;
    private readonly string _directory;
    private readonly string _archive;
    private readonly FileStream _response;
    private readonly StringBuilder _partialText = new();
    private bool _closed;
    private bool _sealed;
    public int? HttpStatus { get; private set; }
    public string? RequestId { get; private set; }
    public string DetailId => _attempt.AttemptId.ToString("N");
    public ImmutableArray<ModelBlock> PartialContent => _partialText.Length == 0 ? [] : [ModelBlock.TextBlock(_partialText.ToString()) with { Complete = false }];

    /// <summary>只创建本次 Guid 目录，不接收外部路径片段。</summary>
    private AttemptDiagnostics(string root, ModelAttemptRequest attempt, IModelUpdateSink? sink)
    {
        _attempt = attempt; _sink = sink;
        _directory = Path.Combine(root, attempt.AttemptId.ToString("N"));
        _archive = _directory + ".zip";
        Directory.CreateDirectory(_directory);
        _response = new FileStream(Path.Combine(_directory, "response.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
    }

    /// <summary>网络执行前保存逻辑输入，即使尚未取得响应也能定位失败尝试。</summary>
    public static async Task<AttemptDiagnostics> CreateAsync(string root, ModelAttemptRequest attempt, IModelUpdateSink? sink, CancellationToken token)
    {
        AttemptDiagnostics detail = new(root, attempt, sink);
        try
        {
            await using FileStream file = File.Create(Path.Combine(detail._directory, "input.json"));
            await JsonSerializer.SerializeAsync(file, attempt, cancellationToken: token).ConfigureAwait(false);
            return detail;
        }
        catch { await detail.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>记录驱动已经移除媒体二进制的实际协议请求。</summary>
    public async Task RequestAsync(JsonElement request, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream file = File.Create(Path.Combine(_directory, "request.json"));
            await JsonSerializer.SerializeAsync(file, request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new ModelRecordingException(); }
    }

    /// <summary>保留实际字节和先后次序，截断响应不伪造终止事件。</summary>
    public async Task ResponseAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try { await _response.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new ModelRecordingException(); }
    }

    /// <summary>记录允许的响应头信息，不记录任意鉴权或 cookie。</summary>
    public void ResponseHeaders(int status, string? requestId) { HttpStatus = status; RequestId = requestId; }

    /// <summary>消费者失败阻止后续候选调用，已经接收的文字仍保留。</summary>
    public async Task UpdateAsync(string kind, string? text, JsonElement? data, CancellationToken cancellationToken)
    {
        if (text is not null) _partialText.Append(text);
        if (_sink is null) return;
        try
        {
            await _sink.OnUpdateAsync(new ModelUpdate
            {
                CallId = _attempt.Request.Id,
                AttemptId = _attempt.AttemptId,
                ModelId = _attempt.Model.Id,
                Kind = kind,
                Text = text,
                Data = data?.Clone()
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ModelConsumerException(); }
    }

    /// <summary>将本次独占文件封装为可读取详情；元数据写入成功后再决定是否删除。</summary>
    public async Task SealAsync(ModelProtocolResult result)
    {
        if (_sealed) return;
        await _response.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        long received = _response.Length;
        await _response.DisposeAsync().ConfigureAwait(false); _closed = true;
        await using (FileStream file = File.Create(Path.Combine(_directory, "outcome.json")))
            await JsonSerializer.SerializeAsync(file, new DetailOutcome { Result = result, HttpStatus = HttpStatus, RequestId = RequestId, ReceivedResponseBytes = received }).ConfigureAwait(false);
        ZipFile.CreateFromDirectory(_directory, _archive, CompressionLevel.Fastest, false);
        Directory.Delete(_directory, true);
        _sealed = true;
    }

    /// <summary>成功且只保存元数据时删除本次详情，不碰其他调用文件。</summary>
    public void Discard() { if (_sealed) File.Delete(_archive); }

    /// <summary>关闭失败途中仍打开的文件；未封装目录保留用于存储故障诊断。</summary>
    public async ValueTask DisposeAsync()
    { if (!_closed) { await _response.DisposeAsync().ConfigureAwait(false); _closed = true; } }

    /// <summary>不存在响应与不完整响应分别以字节数和完成状态表达。</summary>
    private sealed record DetailOutcome
    {
        public required ModelProtocolResult Result { get; init; }
        public int? HttpStatus { get; init; }
        public string? RequestId { get; init; }
        public long ReceivedResponseBytes { get; init; }
    }
}
