using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Missili.Models;

/// <summary>一片压缩后的原件引用及其空间、时间位置，供组批和诊断使用。</summary>
public sealed record PreparedImageSlice
{
    public required Guid MediaId { get; init; }
    public required long Length { get; init; }
    public required string Description { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int FrameIndex { get; init; }
    public double TimestampMilliseconds { get; init; }
}

/// <summary>通过真实编解码器完成方向、透明底、尺寸、切片和动画采样。</summary>
public sealed class ImagePreparation
{
    private readonly IMediaStore _media;
    private readonly IMediaImporter _importer;
    private readonly SemaphoreSlim _slots;

    /// <summary>图片解码拥有独立额度，等待它不会占用模型网络额度。</summary>
    public ImagePreparation(IMediaStore media, IMediaImporter importer, int concurrency)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency); _media = media; _importer = importer; _slots = new SemaphoreSlim(concurrency, concurrency); }

    /// <summary>归档原件保持不变，每片 JPEG 作为独立派生输入保存。</summary>
    public async Task<ImmutableArray<PreparedImageSlice>> PrepareAsync(Guid mediaId, ImagePreparationOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using MediaReadResult read = await _media.OpenReadAsync(mediaId, cancellationToken).ConfigureAwait(false);
            if (read.Content is null) throw new ModelInputException(read.Error ?? "图片原件不可用。");
            if (read.Status.Length > options.MaxSourceBytes) throw new ModelInputException("图片原件超过预处理大小上限。");
            ImageInfo info = await Image.IdentifyAsync(read.Content, cancellationToken).ConfigureAwait(false);
            int frames = Math.Max(1, info.FrameCount);
            if (frames > options.MaxSourceFrames || (long)info.Width * info.Height > options.MaxDecodedPixels / frames)
                throw new ModelInputException("图片解码帧数或像素总量超过上限。");
            string format = info.Metadata.DecodedImageFormat?.Name ?? throw new ModelInputException("不能确定图片格式。");
            if (format is not ("JPEG" or "PNG" or "GIF" or "WEBP" or "BMP")) throw new ModelInputException("不支持该图片格式。");
            read.Content.Position = 0;
            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(new DecoderOptions { MaxFrames = checked((uint)options.MaxSourceFrames + 1) }, read.Content, cancellationToken).ConfigureAwait(false);
            if (image.Frames.Count > options.MaxSourceFrames || (long)image.Width * image.Height > options.MaxDecodedPixels / image.Frames.Count)
                throw new ModelInputException("解码后的动画超出资源限制。");
            ImmutableArray<(int Index, double Time)> samples = SampleFrames(image, format, options);
            ImmutableArray<PreparedImageSlice>.Builder output = ImmutableArray.CreateBuilder<PreparedImageSlice>();
            foreach ((int index, double time) in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 解码器已按帧混合和处置规则合成画面，不能直接使用压缩文件中的局部变化矩形。
                using Image<Rgba32> frame = image.Frames.CloneFrame(index);
                frame.Mutate(context => context.AutoOrient().BackgroundColor(Color.White));
                int originalWidth = frame.Width, originalHeight = frame.Height;
                bool horizontal = frame.Width >= frame.Height;
                bool sliced = (double)Math.Max(frame.Width, frame.Height) / Math.Min(frame.Width, frame.Height) >= options.SliceRatio;
                double scale = sliced
                    ? Math.Min(1, horizontal ? (double)options.MaxHeight / frame.Height : (double)options.MaxWidth / frame.Width)
                    : Math.Min(1, Math.Min((double)options.MaxWidth / frame.Width, (double)options.MaxHeight / frame.Height));
                int width = Math.Max(1, (int)Math.Floor(frame.Width * scale)), height = Math.Max(1, (int)Math.Floor(frame.Height * scale));
                if (width != frame.Width || height != frame.Height) frame.Mutate(context => context.Resize(width, height));
                int longLength = horizontal ? width : height;
                int limit = horizontal ? options.MaxWidth : options.MaxHeight;
                int offset = 0;
                while (true)
                {
                    if (output.Count >= options.MaxSlices) throw new ModelInputException("图片全部帧的切片数量超过上限。");
                    int length = sliced ? Math.Min(limit, longLength - offset) : longLength;
                    Rectangle rectangle = horizontal ? new Rectangle(offset, 0, length, height) : new Rectangle(0, offset, width, length);
                    using Image<Rgba32> tile = frame.Clone(context => context.Crop(rectangle));
                    using MemoryStream encoded = new();
                    await tile.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = options.Quality }, cancellationToken).ConfigureAwait(false);
                    encoded.Position = 0;
                    int x = (int)Math.Floor((double)rectangle.X * originalWidth / width);
                    int y = (int)Math.Floor((double)rectangle.Y * originalHeight / height);
                    int sourceWidth = (int)Math.Ceiling((double)rectangle.Width * originalWidth / width);
                    int sourceHeight = (int)Math.Ceiling((double)rectangle.Height * originalHeight / height);
                    string description = $"{(image.Frames.Count > 1 ? "同一动图" : "图片")}：帧 {index + 1}，时间 {time:F0} 毫秒；原图位置 ({x},{y})，区域 {sourceWidth}×{sourceHeight}。";
                    JsonElement representation = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["format"] = "jpeg",
                        ["original"] = false,
                        ["sourceContentKey"] = read.Status.ContentKey,
                        ["width"] = tile.Width,
                        ["height"] = tile.Height,
                        ["frame"] = index,
                        ["timestampMilliseconds"] = time,
                        ["x"] = x,
                        ["y"] = y,
                        ["sourceWidth"] = sourceWidth,
                        ["sourceHeight"] = sourceHeight
                    });
                    MediaStatus imported = await _importer.ImportAsync(MediaKind.Image, encoded, representation, cancellationToken).ConfigureAwait(false);
                    output.Add(new PreparedImageSlice
                    {
                        MediaId = imported.Id,
                        Length = imported.Length!.Value,
                        Width = tile.Width,
                        Height = tile.Height,
                        FrameIndex = index,
                        TimestampMilliseconds = time,
                        Description = description
                    });
                    if (!sliced || offset + length >= longLength) break;
                    offset += length - options.Overlap;
                }
            }
            return output.ToImmutable();
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
        { throw new ModelInputException("图片格式无效或内容损坏。"); }
        finally { _slots.Release(); }
    }

    /// <summary>依据实际显示时长采样；达到帧上限后覆盖整个播放周期。</summary>
    private static ImmutableArray<(int Index, double Time)> SampleFrames(Image<Rgba32> image, string format, ImagePreparationOptions options)
    {
        if (image.Frames.Count == 1) return [(0, 0)];
        double[] durations = new double[image.Frames.Count];
        for (int index = 0; index < durations.Length; index++)
        {
            ImageFrameMetadata metadata = image.Frames[index].Metadata;
            double duration = format switch
            {
                "GIF" => metadata.GetGifMetadata().FrameDelay * 10.0,
                "PNG" => metadata.GetPngMetadata().FrameDelay.ToDouble() * 1000,
                "WEBP" => metadata.GetWebpMetadata().FrameDelay,
                _ => throw new ModelInputException("该图片格式未提供支持的动画时间信息。")
            };
            durations[index] = double.IsFinite(duration) && duration > 0 ? duration : 10;
        }
        double total = durations.Sum();
        double requested = Math.Ceiling(total * options.FramesPerSecond / 1000);
        int count = (int)Math.Min(options.MaxSampleFrames, Math.Max(1, requested));
        bool capped = requested >= options.MaxSampleFrames;
        ImmutableArray<(int, double)>.Builder result = ImmutableArray.CreateBuilder<(int, double)>();
        int frame = 0; double end = durations[0];
        for (int sample = 0; sample < count; sample++)
        {
            double time = capped && count > 1 ? Math.Min(Math.BitDecrement(total), sample * total / (count - 1)) : sample * 1000 / options.FramesPerSecond;
            while (time >= end && frame < durations.Length - 1) end += durations[++frame];
            if (result.Count == 0 || result[^1].Item1 != frame) result.Add((frame, time));
        }
        return result.ToImmutable();
    }

}
