using System.Collections.Immutable;
using Missili.Abstractions;
using Missili.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>通过真实归档和编解码验证图片预处理，不以尺寸计算代替图像结果。</summary>
public sealed class ImagePreparationTests
{
    /// <summary>普通透明 PNG 等比缩小后重新编码，透明底应实际变为白色。</summary>
    [Fact]
    public async Task TransparentPngIsResizedAndCompositedToJpeg()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using Image<Rgba32> source = new(64, 32, new Rgba32(255, 0, 0, 0));
        Guid id = await ImportAsync(fixture, source, "PNG", token);
        ImagePreparation processor = new(fixture.Runtime.Media, fixture.Runtime.MediaImporter, 1);
        PreparedImageSlice slice = Assert.Single(await processor.PrepareAsync(id, new ImagePreparationOptions { MaxWidth = 32, MaxHeight = 32, Overlap = 4 }, token));
        Assert.Equal(32, slice.Width); Assert.Equal(16, slice.Height);
        await using MediaReadResult read = await fixture.Runtime.Media.OpenReadAsync(slice.MediaId, token);
        using Image<Rgba32> result = await Image.LoadAsync<Rgba32>(read.Content!, token);
        Assert.Equal("JPEG", result.Metadata.DecodedImageFormat!.Name);
        Assert.InRange(result[0, 0].R, (byte)250, (byte)255);
        Assert.InRange(result[0, 0].G, (byte)250, (byte)255);
        Assert.InRange(result[0, 0].B, (byte)250, (byte)255);
        await using MediaReadResult original = await fixture.Runtime.Media.OpenReadAsync(id, token);
        using Image<Rgba32> retained = await Image.LoadAsync<Rgba32>(original.Content!, token);
        Assert.Equal(64, retained.Width); Assert.Equal(0, retained[0, 0].A);
    }

    /// <summary>长图保留整个末尾，切片坐标和重叠对应实际裁剪区域。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongImagesKeepTheTailAndBoundEverySlice(bool horizontal)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using Image<Rgba32> source = new(horizontal ? 100 : 10, horizontal ? 10 : 100, new Rgba32(30, 150, 70));
        Guid id = await ImportAsync(fixture, source, "PNG", token);
        ImagePreparation processor = new(fixture.Runtime.Media, fixture.Runtime.MediaImporter, 1);
        ImmutableArray<PreparedImageSlice> slices = await processor.PrepareAsync(id, new ImagePreparationOptions { MaxWidth = 32, MaxHeight = 32, Overlap = 4 }, token);
        Assert.Equal(4, slices.Length);
        Assert.All(slices, slice => { Assert.InRange(slice.Width, 1, 32); Assert.InRange(slice.Height, 1, 32); });
        MediaStatus last = (await fixture.Runtime.Media.GetStatusAsync(slices[^1].MediaId, token))!;
        int offset = last.Representation!.Value.GetProperty(horizontal ? "x" : "y").GetInt32();
        int length = last.Representation.Value.GetProperty(horizontal ? "sourceWidth" : "sourceHeight").GetInt32();
        Assert.Equal(84, offset); Assert.Equal(100, offset + length);
    }

    /// <summary>三种动画格式都根据显示时长采样，帧上限覆盖末帧而非截取开头。</summary>
    [Theory]
    [InlineData("GIF")]
    [InlineData("PNG")]
    [InlineData("WEBP")]
    public async Task AnimationSamplesWholeDuration(string format)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using Image<Rgba32> source = Animation();
        Guid id = await ImportAsync(fixture, source, format, token);
        ImagePreparation processor = new(fixture.Runtime.Media, fixture.Runtime.MediaImporter, 1);
        ImmutableArray<PreparedImageSlice> slices = await processor.PrepareAsync(id, new ImagePreparationOptions { FramesPerSecond = 2, MaxSampleFrames = 2 }, token);
        Assert.Equal(new[] { 0, 2 }, slices.Select(item => item.FrameIndex));
        Assert.True(slices[1].TimestampMilliseconds > 1900);
        await using MediaReadResult read = await fixture.Runtime.Media.OpenReadAsync(slices[^1].MediaId, token);
        using Image<Rgba32> last = await Image.LoadAsync<Rgba32>(read.Content!, token);
        Assert.True(last[0, 0].G > last[0, 0].R + 100);
        await Assert.ThrowsAsync<ModelInputException>(() => processor.PrepareAsync(id, new ImagePreparationOptions { MaxSourceFrames = 2 }, token));
    }

    /// <summary>构造有不同持续时长的真实动画，所有格式使用相同视觉内容。</summary>
    private static Image<Rgba32> Animation()
    {
        Image<Rgba32> image = new(8, 8, new Rgba32(255, 0, 0));
        using Image<Rgba32> blue = new(8, 8, new Rgba32(0, 0, 255));
        using Image<Rgba32> green = new(8, 8, new Rgba32(0, 255, 0));
        image.Frames.AddFrame(blue.Frames.RootFrame); image.Frames.AddFrame(green.Frames.RootFrame);
        int[] durations = [100, 900, 1000];
        for (int index = 0; index < image.Frames.Count; index++)
        {
            image.Frames[index].Metadata.GetGifMetadata().FrameDelay = durations[index] / 10;
            image.Frames[index].Metadata.GetPngMetadata().FrameDelay = new Rational((uint)durations[index], 1000);
            image.Frames[index].Metadata.GetWebpMetadata().FrameDelay = (uint)durations[index];
        }
        return image;
    }

    /// <summary>标准 APNG 的零分母表示 100，不能把整个动画当作几个零时长帧。</summary>
    [Fact]
    public async Task ApngZeroDenominatorRetainsFrameTiming()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using Image<Rgba32> source = Animation();
        using MemoryStream encoded = new();
        await source.SaveAsPngAsync(encoded, token);
        byte[] png = encoded.ToArray();
        // 编码器会规范化元数据，因此直接构造规范允许的 fcTL，校验值仅服务 PNG 必需的完整性字段。
        for (int offset = 8; offset < png.Length;)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("fcTL"u8))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(png.AsSpan(offset + 28, 2), 100);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(png.AsSpan(offset + 30, 2), 0);
                uint crc = System.IO.Hashing.Crc32.HashToUInt32(png.AsSpan(offset + 4, length + 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 8 + length, 4), crc);
            }
            offset += length + 12;
        }
        using MemoryStream original = new(png);
        Guid id = (await fixture.Runtime.MediaImporter.ImportAsync(MediaKind.Image, original, cancellationToken: token)).Id;
        ImagePreparation processor = new(fixture.Runtime.Media, fixture.Runtime.MediaImporter, 1);
        ImmutableArray<PreparedImageSlice> slices = await processor.PrepareAsync(id, new ImagePreparationOptions(), token);
        Assert.Equal(new[] { 0, 1, 2 }, slices.Select(item => item.FrameIndex));
        Assert.Equal(new[] { 0.0, 1000.0, 2000.0 }, slices.Select(item => item.TimestampMilliseconds));
    }

    /// <summary>实际编解码覆盖静态格式及 EXIF 方向，资源超限必须拒绝整图。</summary>
    [Theory]
    [InlineData("JPEG")]
    [InlineData("BMP")]
    [InlineData("WEBP")]
    public async Task StaticFormatsRespectOrientationAndResourceLimits(string format)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token);
        using Image<Rgba32> source = new(30, 20, new Rgba32(80, 100, 150));
        if (format == "JPEG")
        {
            source.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
            source.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
        }
        Guid id = await ImportAsync(fixture, source, format, token);
        ImagePreparation processor = new(fixture.Runtime.Media, fixture.Runtime.MediaImporter, 1);
        PreparedImageSlice slice = Assert.Single(await processor.PrepareAsync(id, new ImagePreparationOptions(), token));
        Assert.Equal(format == "JPEG" ? 20 : 30, slice.Width);
        Assert.Equal(format == "JPEG" ? 30 : 20, slice.Height);
        await Assert.ThrowsAsync<ModelInputException>(() => processor.PrepareAsync(id, new ImagePreparationOptions { MaxDecodedPixels = 599 }, token));
        await Assert.ThrowsAsync<ModelInputException>(() => processor.PrepareAsync(id, new ImagePreparationOptions { MaxSourceBytes = 1 }, token));
        await Assert.ThrowsAsync<ArgumentException>(() => processor.PrepareAsync(id, new ImagePreparationOptions { Overlap = 2048 }, token));
    }

    /// <summary>使用生产媒体导入路径保存合成格式，返回真实库内引用。</summary>
    internal static async Task<Guid> ImportAsync(RuntimeFixture fixture, Image<Rgba32> image, string format, CancellationToken token)
    {
        using MemoryStream bytes = new();
        if (format == "PNG") await image.SaveAsPngAsync(bytes, token);
        else if (format == "GIF") await image.SaveAsGifAsync(bytes, token);
        else if (format == "WEBP") await image.SaveAsWebpAsync(bytes, token);
        else if (format == "JPEG") await image.SaveAsJpegAsync(bytes, token);
        else if (format == "BMP") await image.SaveAsBmpAsync(bytes, token);
        else throw new ArgumentException("未知测试格式。");
        bytes.Position = 0;
        return (await fixture.Runtime.MediaImporter.ImportAsync(MediaKind.Image, bytes, cancellationToken: token)).Id;
    }
}
