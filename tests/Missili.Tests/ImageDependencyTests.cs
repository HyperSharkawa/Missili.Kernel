using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>从 Bot 实际依赖路径验证源码构建包能够完成图片解码和 JPEG 编码。</summary>
public sealed class ImageDependencyTests
{
    /// <summary>使用合成 PNG 经实际包转换为 JPEG，验证尺寸、格式和像素，而不依赖上游 LFS 素材。</summary>
    [Fact]
    public async Task SourceBuiltPackageConvertsPngToJpeg()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Rgba32 expected = new(24, 92, 167, 255);
        using Image<Rgba32> original = new(12, 8, expected);
        using MemoryStream png = new();
        await original.SaveAsPngAsync(png, token);
        png.Position = 0;
        using Image<Rgba32> decoded = await Image.LoadAsync<Rgba32>(png, token);
        Assert.Equal(expected, decoded[3, 4]);

        using MemoryStream jpeg = new();
        await decoded.SaveAsJpegAsync(jpeg, new JpegEncoder { Quality = 85 }, token);
        jpeg.Position = 0;
        using Image<Rgba32> result = await Image.LoadAsync<Rgba32>(jpeg, token);
        Assert.Equal(12, result.Width);
        Assert.Equal(8, result.Height);
        Assert.Equal("JPEG", result.Metadata.DecodedImageFormat!.Name);
        // JPEG 是有损编码，检查颜色误差范围，不能要求压缩后字节或像素完全一致。
        Assert.InRange(Math.Abs(result[3, 4].R - expected.R), 0, 5);
        Assert.InRange(Math.Abs(result[3, 4].G - expected.G), 0, 5);
        Assert.InRange(Math.Abs(result[3, 4].B - expected.B), 0, 5);
    }
}
