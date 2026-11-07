using System.Collections.Immutable;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Models;

/// <summary>有界的进程内转述执行器；数据库只保存结果和失败信息，不保存执行游标。</summary>
public sealed class MediaTranscriptionService : IMediaTranscriptionService, IAsyncDisposable
{
    private readonly IMediaStore _media;
    private readonly IMediaTranscriptionStore _cache;
    private readonly ImagePreparation _images;
    private readonly IMediaRepresentationService _audio;
    private readonly ModelCoordinator _models;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly int _concurrency;
    private readonly Channel<Work> _queue;
    private readonly SemaphoreSlim _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), Work> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private Task[] _workers = [];
    private bool _closing;

    /// <summary>所有执行属于宿主，业务插件仅取得结果等待权。</summary>
    public MediaTranscriptionService(IMediaStore media, IMediaTranscriptionStore cache, ImagePreparation images,
        IMediaRepresentationService audio, ModelCoordinator models, ModelOptions options, TimeProvider clock, ILogger logger)
    {
        _media = media; _cache = cache; _images = images; _audio = audio; _models = models; _clock = clock; _logger = logger;
        _concurrency = options.TranscriptionConcurrency; _capacity = new SemaphoreSlim(options.TranscriptionCapacity, options.TranscriptionCapacity);
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(options.TranscriptionCapacity) { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }

    /// <summary>基础设施和配置就绪后才启动固定数量的工作循环。</summary>
    public void Start()
    {
        if (_workers.Length != 0 || _closing) throw new InvalidOperationException("转述服务不能重复启动。");
        _workers = Enumerable.Range(0, _concurrency).Select(_ => Task.Run(ConsumeAsync)).ToArray();
    }

    /// <summary>普通读取不触发模型请求，也不自动重试失败内容。</summary>
    public async Task<TranscriptionSnapshot?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        MediaStatus? status = await _media.GetStatusAsync(mediaId, cancellationToken).ConfigureAwait(false);
        string? kind = status is null ? null : Kind(status.Kind);
        return status?.ContentKey is null || kind is null ? null : await _cache.GetTranscriptionAsync(status.ContentKey, kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取消当前等待不取消已入队的共享转述，刷新仍按缓存修订保护。</summary>
    public async Task<MediaTranscriptionResult> EnsureAsync(Guid mediaId, ConversationProfileSnapshot profile, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_workers.Length == 0) throw new InvalidOperationException("转述服务尚未启动。");
        MediaStatus? status = await _media.GetStatusAsync(mediaId, cancellationToken).ConfigureAwait(false);
        if (status is null || Kind(status.Kind) is not string kind) return new MediaTranscriptionResult { Error = "该媒体不支持本阶段转述。" };
        TranscriptionSnapshot? cached = status.ContentKey is null ? null : await _cache.GetTranscriptionAsync(status.ContentKey, kind, cancellationToken).ConfigureAwait(false);
        if (cached?.Text is not null && !refresh) return new MediaTranscriptionResult { Cached = cached };
        bool enabled = status.Kind == MediaKind.Image ? profile.Profile.ImageTranscriptionEnabled : profile.Profile.AudioTranscriptionEnabled;
        if (!enabled) return new MediaTranscriptionResult { Cached = cached, Error = "当前配置未启用此类媒体转述。" };
        if (status.State == MediaState.Cleaned) return new MediaTranscriptionResult { Cached = cached, Error = "原件已清理、暂无可用新转述。" };
        if (status.State != MediaState.Available)
        {
            await using MediaReadResult read = await _media.OpenReadAsync(mediaId, cancellationToken).ConfigureAwait(false);
            if (read.Content is null) return new MediaTranscriptionResult { Cached = cached, Error = read.Error ?? "媒体原件不可用。" };
            status = read.Status;
            cached = await _cache.GetTranscriptionAsync(status.ContentKey!, kind, cancellationToken).ConfigureAwait(false);
            if (cached?.Text is not null && !refresh) return new MediaTranscriptionResult { Cached = cached };
        }
        (string, string) key = (status.ContentKey!, kind);
        Work? existing;
        lock (_gate) _pending.TryGetValue(key, out existing);
        if (existing is not null) return await existing.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _capacity.WaitAsync(admission.Token).ConfigureAwait(false);
        Work work;
        lock (_gate)
        {
            if (_closing) { _capacity.Release(); throw new OperationCanceledException(_stop.Token); }
            if (_pending.TryGetValue(key, out existing)) { _capacity.Release(); work = existing; }
            else
            {
                work = new Work { Media = status, Kind = kind, Profile = profile, Previous = cached };
                _pending.Add(key, work);
                // 容量已提前取得，此处不会在队列外创建另一批等待任务。
                if (!_queue.Writer.TryWrite(work)) { _pending.Remove(key); _capacity.Release(); throw new InvalidOperationException("转述队列已关闭。"); }
            }
        }
        return await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>固定工作数执行转述；失败结束当前执行，不进入自动轮询重试。</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (Work work in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _capacity.Release();
                MediaTranscriptionResult? result = null;
                try { result = await ExecuteAsync(work, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    _logger.LogError("转述基础设施失败：{ErrorType}", exception.GetType().Name);
                    result = new MediaTranscriptionResult { Cached = work.Previous, Error = "媒体转述失败，内容暂不可用。" };
                }
                finally { lock (_gate) _pending.Remove((work.Media.ContentKey!, work.Kind)); }
                // 先撤销共享执行，再唤醒等待者，紧接着发起的主动刷新才能创建新执行。
                if (result is not null) work.Completion.TrySetResult(result);
                else work.Completion.TrySetCanceled(_stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>只有所有必要请求完整成功才写成功缓存，失败刷新保留旧文字。</summary>
    private async Task<MediaTranscriptionResult> ExecuteAsync(Work work, CancellationToken token)
    {
        string? error = null;
        string? partial = null;
        try
        {
            (string? text, ImmutableArray<TranscriptionSource> sources, string? failure, string? incomplete) =
                work.Media.Kind == MediaKind.Image ? await DescribeAsync(work, token).ConfigureAwait(false) : await TranscribeAudioAsync(work, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            error = failure; partial = incomplete;
            if (text is not null)
            {
                TranscriptionSnapshot saved = await _cache.SaveTranscriptionAsync(new TranscriptionSnapshot
                { ContentKey = work.Media.ContentKey!, Kind = work.Kind, Text = text, Sources = sources, CompletedAt = _clock.GetUtcNow() }, work.Previous?.Revision ?? 0, token).ConfigureAwait(false);
                return new MediaTranscriptionResult { Cached = saved };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (RevisionConflictException) { return new MediaTranscriptionResult { Cached = await _cache.GetTranscriptionAsync(work.Media.ContentKey!, work.Kind, token).ConfigureAwait(false), Error = "转述结果已由其他调用更新。" }; }
        catch (Exception exception) when (exception is ModelInputException or MediaAcquisitionException or AdapterUnavailableException or IOException or NotSupportedException)
        { error = exception.Message; }
        TranscriptionSnapshot failed = await _cache.RecordTranscriptionFailureAsync(work.Media.ContentKey!, work.Kind,
            error ?? "转述没有取得完整文本。", work.Previous?.Revision ?? 0, token).ConfigureAwait(false);
        return new MediaTranscriptionResult { Cached = failed, PartialText = partial, Error = error ?? "媒体转述失败，内容暂不可用。" };
    }

    /// <summary>一个候选按自身限制组批；换候选时完整重新组批，不截断原图内容。</summary>
    private async Task<(string?, ImmutableArray<TranscriptionSource>, string?, string?)> DescribeAsync(Work work, CancellationToken token)
    {
        ImmutableArray<PreparedImageSlice> slices = await _images.PrepareAsync(work.Media.Id, work.Profile.Profile.ImagePreparation, token).ConfigureAwait(false);
        ModelExecutionOptions policy = work.Profile.Profile.Execution;
        string? lastError = null, partial = null;
        for (int round = 1; round <= policy.Rounds; round++)
        {
            foreach (string candidate in work.Profile.Profile.ImageModels)
            {
                token.ThrowIfCancellationRequested();
                ConversationProfileSnapshot profile = SingleAttempt(work.Profile, candidate);
                ModelDefinition model = profile.Models[candidate];
                List<string> parts = [];
                ImmutableArray<TranscriptionSource>.Builder sources = ImmutableArray.CreateBuilder<TranscriptionSource>();
                bool failed = false;
                int offset = 0;
                try
                {
                    // 每批独立记录实际调用；外层负责整图候选预算，内层禁止再重试一次。
                    while (offset < slices.Length)
                    {
                        int count = 0;
                        ModelRequest? batch = null;
                        while (offset + count < slices.Length && count < model.MaxImages)
                        {
                            ModelRequest next = ImageRequest(profile, candidate, slices.Slice(offset, count + 1));
                            long bytes = await _models.MeasureAsync(next, candidate, token).ConfigureAwait(false);
                            if (bytes > model.MaxRequestBytes) break;
                            count++; batch = next;
                        }
                        if (batch is null) throw new ModelInputException("单个图片切片无法装入候选的请求体预算。");
                        ModelResult result = await _models.GenerateAsync(batch, cancellationToken: token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (result.State != ModelCallState.Succeeded || !result.Complete || string.IsNullOrWhiteSpace(result.Text) || result.Content.Any(item => item.Kind == ModelBlockKind.ToolCall))
                        {
                            partial = string.Join("\n\n", parts.Append(result.Text)); lastError = result.Error ?? "图片转述未返回完整文本。";
                            if (MustStop(result)) return (null, [], lastError, partial);
                            failed = true; break;
                        }
                        parts.Add($"区域 {offset + 1} 至 {offset + count}：\n{result.Text}");
                        sources.Add(new TranscriptionSource { Provider = model.ProviderId, Model = model.Name, Prompt = profile.Profile.ImagePromptVersion, Batch = sources.Count, CallId = result.CallId });
                        offset += count;
                    }
                }
                catch (ModelInputException exception) { lastError = exception.Message; failed = true; }
                if (!failed) return (string.Join("\n\n", parts), sources.ToImmutable(), null, null);
            }
            if (round < policy.Rounds) await Task.Delay(policy.RoundDelay, _clock, token).ConfigureAwait(false);
        }
        return (null, [], lastError, partial);
    }

    /// <summary>语音按候选准备格式和流式模式，失败不将旧结果覆盖成成功。</summary>
    private async Task<(string?, ImmutableArray<TranscriptionSource>, string?, string?)> TranscribeAudioAsync(Work work, CancellationToken token)
    {
        string? error = null, partial = null;
        ModelExecutionOptions policy = work.Profile.Profile.Execution;
        for (int round = 1; round <= policy.Rounds; round++)
        {
            foreach (string candidate in work.Profile.Profile.TranscriptionModels)
            {
                ConversationProfileSnapshot profile = SingleAttempt(work.Profile, candidate);
                ModelDefinition model = profile.Models[candidate];
                try
                {
                    MediaRepresentation representation = await _audio.PrepareAudioAsync(work.Media.Id, model, token).ConfigureAwait(false);
                    ModelResult result = await _models.GenerateAsync(new ModelRequest
                    {
                        Purpose = "audio.transcription",
                        Profile = profile,
                        Candidates = [candidate],
                        Stream = model.Streaming,
                        Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [new ModelBlock { Kind = ModelBlockKind.Audio, MediaId = representation.MediaId, MimeType = representation.MimeType }] }])
                    }, cancellationToken: token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (result.State == ModelCallState.Succeeded && result.Complete && !string.IsNullOrWhiteSpace(result.Text))
                        return (result.Text, [new TranscriptionSource { Provider = model.ProviderId, Model = model.Name, CallId = result.CallId }], null, null);
                    error = result.Error ?? "语音转写未返回完整文本。"; partial = result.Text;
                    if (MustStop(result)) return (null, [], error, partial);
                }
                catch (Exception exception) when (exception is ModelInputException or MediaAcquisitionException or AdapterUnavailableException) { error = exception.Message; }
            }
            if (round < policy.Rounds) await Task.Delay(policy.RoundDelay, _clock, token).ConfigureAwait(false);
        }
        return (null, [], error, partial);
    }

    /// <summary>本地错误与主动取消不能在整图的外层循环中重新变成模型重试。</summary>
    private static bool MustStop(ModelResult result) => result.State is ModelCallState.RecordingFailed or ModelCallState.Cancelled ||
        result.Attempts.Any(attempt => attempt.ErrorKind is "Input" or "Consumer" or "Infrastructure");

    /// <summary>外层候选循环拥有预算，批次调用只执行一次；同时收紧图片体积和数量限制。</summary>
    private static ConversationProfileSnapshot SingleAttempt(ConversationProfileSnapshot snapshot, string candidate)
    {
        ModelDefinition model = snapshot.Models[candidate];
        ImagePreparationOptions image = snapshot.Profile.ImagePreparation;
        return snapshot with
        {
            Profile = snapshot.Profile with { Execution = snapshot.Profile.Execution with { Rounds = 1 } },
            Models = snapshot.Models.SetItem(candidate, model with { MaxImages = Math.Min(model.MaxImages, image.MaxImagesPerRequest), MaxRequestBytes = Math.Min(model.MaxRequestBytes, image.MaxRequestBytes) })
        };
    }

    /// <summary>每片附加位置说明，结果按同一空间次序组合，不额外调用总结模型。</summary>
    private static ModelRequest ImageRequest(ConversationProfileSnapshot profile, string candidate, ImmutableArray<PreparedImageSlice> slices)
    {
        ImmutableArray<ModelBlock>.Builder content = ImmutableArray.CreateBuilder<ModelBlock>();
        content.Add(ModelBlock.TextBlock(profile.Profile.ImagePrompt));
        foreach (PreparedImageSlice slice in slices)
        { content.Add(ModelBlock.TextBlock(slice.Description)); content.Add(new ModelBlock { Kind = ModelBlockKind.Image, MediaId = slice.MediaId, MimeType = "image/jpeg" }); }
        return new ModelRequest
        {
            Purpose = "image.description",
            Profile = profile,
            Candidates = [candidate],
            Stream = profile.Models[candidate].Streaming,
            Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = content.ToImmutable() }])
        };
    }

    /// <summary>种类与成功缓存键固定，模型或提示词变化不参与缓存身份。</summary>
    internal static string? Kind(MediaKind kind) => kind switch { MediaKind.Image => "image.description", MediaKind.Audio => "audio.transcription", _ => null };

    /// <summary>关闭时只取消进程内工作，不保存执行游标或剩余重试预算。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_closing) return; _closing = true; }
        await _stop.CancelAsync().ConfigureAwait(false); _queue.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        while (_queue.Reader.TryRead(out Work? work))
        { _capacity.Release(); work.Completion.TrySetCanceled(_stop.Token); lock (_gate) _pending.Remove((work.Media.ContentKey!, work.Kind)); }
        _stop.Dispose(); _capacity.Dispose();
    }

    /// <summary>一个共享执行只保存到本进程退出，成功缓存由独立存储服务持有。</summary>
    private sealed record Work
    {
        public required MediaStatus Media { get; init; }
        public required string Kind { get; init; }
        public required ConversationProfileSnapshot Profile { get; init; }
        public TranscriptionSnapshot? Previous { get; init; }
        public TaskCompletionSource<MediaTranscriptionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
