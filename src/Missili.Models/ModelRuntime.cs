using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Models;

/// <summary>组装模型协调、上下文和转述，宿主关闭时统一结束其框架工作。</summary>
public sealed class ModelRuntime : IModelModule
{
    private readonly ModelOptions _options;
    private readonly IModelStore _store;
    private readonly IMediaStore _media;
    private readonly IConversationProfileService _profiles;
    private readonly ModelCoordinator _coordinator;
    private readonly MediaTranscriptionService _transcriptions;
    private readonly ConversationContextService _contexts;
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private Task? _scan;
    private Task? _stopping;
    private bool _started;
    private readonly object _gate = new();
    public IConversationContextService Contexts => _contexts;
    public IMediaTranscriptionService Transcriptions => _transcriptions;
    public IMediaRepresentationService Representations { get; }
    public IModelCallHistory Calls => _coordinator;

    /// <summary>模块间通过契约组装，模型请求循环与转述协调的依赖方向保持明确。</summary>
    public ModelRuntime(IInfrastructureStore infrastructure, IModelStore store, IMediaStore media, IMediaImporter importer,
        IAdapterRuntime adapters, IStructuredContentStore contents, IModelProviderRuntime providers, IConversationProfileService profiles, ModelOptions options, string dataDirectory,
        Guid runId, ILoggerFactory loggers, TimeProvider clock)
    {
        options.Validate(); _options = options; _store = store; _media = media; _logger = loggers.CreateLogger<ModelRuntime>();
        _profiles = profiles;
        _coordinator = new ModelCoordinator(store, providers, options, dataDirectory, runId, _logger, clock);
        Representations = new MediaRepresentationService(infrastructure, store, media, importer, adapters);
        ImagePreparation images = new(media, importer, options.ImageConcurrency);
        _transcriptions = new MediaTranscriptionService(media, infrastructure, images, Representations, _coordinator, options, clock, _logger);
        _contexts = new ConversationContextService(store, infrastructure, profiles, media, _transcriptions, Representations, contents);
    }

    /// <summary>存储迁移成功后启动工作；默认关闭不扫描媒体或请求模型。</summary>
    public void Start()
    {
        if (!_options.Enabled) return;
        if (_started) throw new InvalidOperationException("模型模块已启动。");
        _transcriptions.Start(); _started = true;
    }

    /// <summary>在启用插件发布之后启动历史扫描，普通聊天事件不会因此重放。</summary>
    public void StartBackgroundScan()
    {
        if (!_started) return;
        if (_scan is not null) throw new InvalidOperationException("转述扫描已启动。");
        _scan = ScanAsync(); NotifyMediaChanged();
    }

    /// <summary>一个有界信号合并所有归档完成通知，不为每个文件创建后台任务。</summary>
    public void NotifyMediaChanged() { if (_started) _changes.Writer.TryWrite(0); }

    /// <summary>显式模型入口在获取网络额度之前进行候选媒体投影。</summary>
    public Task<ModelResult> GenerateAsync(ModelRequest request, IModelUpdateSink? updates = null, CancellationToken cancellationToken = default,
        CancellationToken admissionCancellationToken = default)
    {
        if (!_started) throw new InvalidOperationException("模型模块未启用或尚未就绪。");
        ModelInput input = request.Input.Freeze();
        return _coordinator.GeneratePreparedAsync(request with { Input = input },
            (model, token) => ProjectInputAsync(input, model, token), updates, cancellationToken, admissionCancellationToken);
    }

    /// <summary>依次投影聊天切片和真实轮次，媒体准备发生在网络额度之外。</summary>
    private async Task<ImmutableArray<ModelMessage>> ProjectInputAsync(ModelInput input, ModelDefinition model, CancellationToken token)
    {
        ImmutableArray<ModelMessage>.Builder messages = ImmutableArray.CreateBuilder<ModelMessage>();
        if (input.Instructions.Length > 0)
            messages.Add(new ModelMessage { Role = "system", Content = [ModelBlock.TextBlock(input.Instructions)] });
        foreach (ModelInputItem item in input.Items)
        {
            token.ThrowIfCancellationRequested();
            if (item is ModelTurnInput turn) messages.Add(turn.Message);
            else if (item is ConversationSliceInput slice)
                messages.AddRange(await _contexts.ProjectAsync(slice.Conversation, model, token).ConfigureAwait(false));
            else throw new ModelInputException("模型输入项类型无效。");
        }
        return messages.ToImmutable();
    }

    /// <summary>有限分页扫描按当前配置决定是否转述，失败只在本进程记为已尝试。</summary>
    private async Task ScanAsync()
    {
        HashSet<(string, string)> attempted = [];
        HashSet<Task<MediaTranscriptionResult>> active = [];
        try
        {
            await foreach (byte change in _changes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_changes.Reader.TryRead(out byte unused)) { }
                long after = 0;
                while (true)
                {
                    TranscriptionMediaPage page = await _store.ScanTranscriptionMediaAsync(after, 64, _stop.Token).ConfigureAwait(false);
                    foreach (TranscriptionMedia item in page.Items)
                    {
                        ConversationProfileSnapshot profile = _profiles.Resolve(item.Address);
                        bool enabled = item.Kind == MediaKind.Image
                            ? profile.Profile.ImageTranscriptionEnabled && profile.Profile.Images == ModelMediaPolicy.TranscriptionPreferred
                            : item.Kind == MediaKind.Audio && profile.Profile.AudioTranscriptionEnabled && profile.Profile.Audio == ModelMediaPolicy.TranscriptionPreferred;
                        if (!enabled) continue;
                        MediaStatus? status = await _media.GetStatusAsync(item.MediaId, _stop.Token).ConfigureAwait(false);
                        if (status?.State != MediaState.Available || status.ContentKey is null) continue;
                        string kind = MediaTranscriptionService.Kind(item.Kind)!;
                        if (attempted.Contains((status.ContentKey, kind))) continue;
                        TranscriptionSnapshot? cached = await _transcriptions.GetAsync(item.MediaId, _stop.Token).ConfigureAwait(false);
                        if (cached?.Text is not null) continue;
                        attempted.Add((status.ContentKey, kind));
                        // 扫描最多持有队列容量加工作数个等待任务，不能为全部历史预建 Task。
                        if (active.Count >= _options.TranscriptionCapacity + _options.TranscriptionConcurrency)
                        { Task<MediaTranscriptionResult> finished = await Task.WhenAny(active).ConfigureAwait(false); await finished.ConfigureAwait(false); active.Remove(finished); }
                        active.Add(_transcriptions.EnsureAsync(item.MediaId, profile, cancellationToken: _stop.Token));
                        foreach (Task<MediaTranscriptionResult> finished in active.Where(task => task.IsCompleted).ToArray())
                        { await finished.ConfigureAwait(false); active.Remove(finished); }
                    }
                    if (!page.NextMessageId.HasValue) break;
                    after = page.NextMessageId.Value;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogError("后台转述扫描停止：{ErrorType}", exception.GetType().Name); }
        finally
        {
            foreach (Task<MediaTranscriptionResult> task in active)
                try { await task.ConfigureAwait(false); } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
    }

    /// <summary>多处宿主关闭入口共享同一停止任务，存储资源由调用方最后释放。</summary>
    public Task StopAsync() { lock (_gate) return _stopping ??= StopCoreAsync(); }

    /// <summary>同时取消转述与模型调用，避免一方等待另一方的网络请求。</summary>
    private async Task StopCoreAsync()
    {
        _started = false;
        await _stop.CancelAsync().ConfigureAwait(false); _changes.Writer.TryComplete();
        Task transcriptions = _transcriptions.DisposeAsync().AsTask();
        Task calls = _coordinator.DisposeAsync().AsTask();
        await Task.WhenAll(transcriptions, calls).ConfigureAwait(false);
        if (_scan is not null) await _scan.ConfigureAwait(false);
        _stop.Dispose();
    }
}
