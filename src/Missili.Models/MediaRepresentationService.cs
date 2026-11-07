using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Models;

/// <summary>识别实际音频容器并通过适配器取得 WAV，模型模块不包含 OneBot 操作名。</summary>
public sealed class MediaRepresentationService : IMediaRepresentationService
{
    private readonly IInfrastructureStore _infrastructure;
    private readonly IModelStore _store;
    private readonly IMediaStore _media;
    private readonly IMediaImporter _importer;
    private readonly IAdapterRuntime _adapters;

    /// <summary>依赖共享能力，不引用适配器实现程序集。</summary>
    public MediaRepresentationService(IInfrastructureStore infrastructure, IModelStore store, IMediaStore media, IMediaImporter importer, IAdapterRuntime adapters)
    { _infrastructure = infrastructure; _store = store; _media = media; _importer = importer; _adapters = adapters; }

    /// <summary>可直接读取的原格式优先；派生转换始终关联原内容键。</summary>
    public async Task<MediaRepresentation> PrepareAudioAsync(Guid mediaId, ModelDefinition model, CancellationToken cancellationToken = default)
    {
        MediaWork work = await _infrastructure.GetMediaAsync(mediaId, cancellationToken).ConfigureAwait(false) ?? throw new ModelInputException("语音引用不存在。");
        await using MediaReadResult original = await _media.OpenReadAsync(mediaId, cancellationToken).ConfigureAwait(false);
        if (original.Content is null) throw new ModelInputException(original.Error ?? "语音原件不可用。");
        string key = original.Status.ContentKey!;
        (string? format, string? mime) = await DetectAsync(original.Content, cancellationToken).ConfigureAwait(false);
        if (format is not null && model.AudioFormats.Contains(format))
            return new MediaRepresentation { MediaId = mediaId, MimeType = mime!, OriginalContentKey = key };
        if (!model.AudioFormats.Contains("wav")) throw new ModelInputException("候选不支持原件格式或本阶段可提供的 WAV 派生格式。");
        Guid? existing = await _store.GetRepresentationAsync(key, "audio.wav", cancellationToken).ConfigureAwait(false);
        if (existing.HasValue) return new MediaRepresentation { MediaId = existing.Value, MimeType = "audio/wav", OriginalContentKey = key };
        if (work.Source.AdapterId is null) throw new ModelInputException("音频格式需要转换，但来源没有可用的适配器转换能力。");
        await using MediaSourceRead converted = await _adapters.ResolveRepresentationAsync(work.Source, "wav", cancellationToken).ConfigureAwait(false);
        if (converted.Content is null) throw new ModelInputException("适配器转换能力没有提供可读取文件。");
        MediaStatus imported = await _importer.ImportAsync(MediaKind.Audio, converted.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using MediaReadResult verification = await _media.OpenReadAsync(imported.Id, cancellationToken).ConfigureAwait(false);
        if (verification.Content is null || (await DetectAsync(verification.Content, cancellationToken).ConfigureAwait(false)).Format != "wav")
            throw new ModelInputException("适配器未返回有效的 WAV 文件，不能以改名代替转码。");
        await _infrastructure.SetMediaRepresentationAsync(imported.Id, JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { ["format"] = "wav", ["original"] = false, ["sourceContentKey"] = key, ["source"] = "adapter.representation" }), cancellationToken).ConfigureAwait(false);
        Guid id = await _store.SaveRepresentationAsync(key, "audio.wav", imported.Id, cancellationToken).ConfigureAwait(false);
        return new MediaRepresentation { MediaId = id, MimeType = "audio/wav", OriginalContentKey = key };
    }

    /// <summary>读取实际容器标识，不能仅相信远端扩展名或 MIME 声明。</summary>
    private static async Task<(string? Format, string? Mime)> DetectAsync(Stream source, CancellationToken token)
    {
        byte[] header = new byte[12];
        int count = await source.ReadAtLeastAsync(header, 12, throwOnEndOfStream: false, cancellationToken: token).ConfigureAwait(false);
        if (count >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return ("wav", "audio/wav");
        if (count >= 4 && header.AsSpan(0, 4).SequenceEqual("fLaC"u8)) return ("flac", "audio/flac");
        if (count >= 4 && header.AsSpan(0, 4).SequenceEqual("OggS"u8)) return ("ogg", "audio/ogg");
        if (count >= 3 && header.AsSpan(0, 3).SequenceEqual("ID3"u8) || count >= 2 && header[0] == 255 && (header[1] & 0xE0) == 0xE0) return ("mp3", "audio/mpeg");
        if (count >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return ("m4a", "audio/mp4");
        if (count >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return ("webm", "audio/webm");
        return (null, null);
    }
}
