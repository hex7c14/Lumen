using Lumen.Core.Imaging;

namespace Lumen.Core.Tests;

public sealed class SkiaImageCodecTests
{
    [Fact]
    public void Resize_ProducesRequestedDimensions()
    {
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(100, 50);

        ImagePixels resized = codec.Resize(source, 40, 20);

        Assert.Equal(40, resized.Width);
        Assert.Equal(20, resized.Height);
        Assert.Equal(40 * 20 * ImagePixels.BytesPerPixel, resized.Pixels.Length);
    }

    [Fact]
    public void Resize_SameSize_ReturnsSameInstance()
    {
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(16, 16);

        Assert.Same(source, codec.Resize(source, 16, 16));
    }

    [Fact]
    public void Resize_NearestKeepsSourceColors()
    {
        var codec = new SkiaImageCodec();
        var source = new ImagePixels(2, 1);
        source.Pixels[0] = 10;
        source.Pixels[1] = 20;
        source.Pixels[2] = 30;
        source.Pixels[3] = 255;
        source.Pixels[4] = 200;
        source.Pixels[5] = 210;
        source.Pixels[6] = 220;
        source.Pixels[7] = 255;

        ImagePixels upscaled = codec.Resize(source, 4, 2, ResampleMode.Nearest);

        Assert.Equal(4, upscaled.Width);
        Assert.Equal(2, upscaled.Height);
        Assert.Equal((byte)10, upscaled.Pixels[0]);
        Assert.Equal((byte)200, upscaled.Pixels[12]);
    }

    [Theory]
    [InlineData(4000, 3000, 2560)]
    [InlineData(1000, 4000, 2560)]
    [InlineData(640, 480, 640)]
    public void CreatePreview_LimitsLongEdge(int width, int height, int expectedLongEdge)
    {
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(width, height);

        ImagePixels preview = codec.CreatePreview(source, 2560, 2560);

        Assert.Equal(expectedLongEdge, Math.Max(preview.Width, preview.Height));
        if (width >= height)
        {
            Assert.Equal(expectedLongEdge, preview.Width);
        }
        else
        {
            Assert.Equal(expectedLongEdge, preview.Height);
        }
    }

    [Fact]
    public void CreatePreview_AlreadySmall_ReturnsSameInstance()
    {
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(100, 50);

        Assert.Same(source, codec.CreatePreview(source, 2560, 2560));
    }

    [Fact]
    public void SaveAndLoadPng_RoundTripsPixels()
    {
        using var temp = new TempDirectory();
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(64, 48);
        string path = temp.Combine("roundtrip.png");

        codec.Save(source, path, SaveOptions.Default);
        LoadedImage loaded = codec.Load(path);

        Assert.Equal(64, loaded.Width);
        Assert.Equal(48, loaded.Height);
        Assert.False(loaded.HasTransparency);
        Assert.Equal(source.Pixels, loaded.Pixels.Pixels);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void SaveAndLoadPng_PreservesTransparency()
    {
        using var temp = new TempDirectory();
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(32, 32, alpha: 128);
        source.Pixels[3] = 0;
        string path = temp.Combine("transparent.png");

        codec.Save(source, path, SaveOptions.Default);
        LoadedImage loaded = codec.Load(path);

        Assert.True(loaded.HasTransparency);
        Assert.Equal((byte)0, loaded.Pixels.Pixels[3]);
        Assert.Equal((byte)128, loaded.Pixels.Pixels[7]);
    }

    [Theory]
    [InlineData(OutputFormat.Jpeg, "photo.jpg")]
    [InlineData(OutputFormat.WebP, "photo.webp")]
    public void SaveEncodesOtherFormats_AndCanReload(OutputFormat format, string fileName)
    {
        using var temp = new TempDirectory();
        var codec = new SkiaImageCodec();
        ImagePixels source = TestImages.CreateGradient(48, 32);
        string path = temp.Combine(fileName);

        codec.Save(source, path, new SaveOptions(format, Quality: 90));
        LoadedImage loaded = codec.Load(path);

        Assert.Equal(48, loaded.Width);
        Assert.Equal(32, loaded.Height);
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void SaveJpeg_FlattensTransparencyOntoWhite()
    {
        using var temp = new TempDirectory();
        var codec = new SkiaImageCodec();
        var source = new ImagePixels(2, 1);
        // 两个像素都是完全透明的红色：RGB 保留 255,0,0，但 alpha=0，JPEG 应合成到白底。
        source.Pixels[0] = 255;
        source.Pixels[3] = 0;
        source.Pixels[7] = 0;
        string path = temp.Combine("transparent.jpg");

        codec.Save(source, path, new SaveOptions(OutputFormat.Jpeg, Quality: 100));
        LoadedImage loaded = codec.Load(path);

        Assert.False(loaded.HasTransparency);
        Assert.InRange((int)loaded.Pixels.Pixels[0], 240, 255);
        Assert.InRange((int)loaded.Pixels.Pixels[1], 240, 255);
        Assert.InRange((int)loaded.Pixels.Pixels[2], 240, 255);
    }

    [Fact]
    public void Load_MissingFile_ThrowsIoException()
    {
        var codec = new SkiaImageCodec();
        Assert.Throws<FileNotFoundException>(
            () => codec.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-lumen.png")));
    }

    [Fact]
    public void DecodeExtensions_ContainsCommonFormats()
    {
        var codec = new SkiaImageCodec();
        Assert.Contains(".png", codec.SupportedDecodeExtensions);
        Assert.Contains(".jpg", codec.SupportedDecodeExtensions);
        Assert.Contains(".webp", codec.SupportedDecodeExtensions);
    }
}
