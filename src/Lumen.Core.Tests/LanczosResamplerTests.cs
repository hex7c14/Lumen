using Lumen.Core.Imaging;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Tests;

public sealed class LanczosResamplerTests
{
    [Fact]
    public void Resize_ProducesRequestedDimensions()
    {
        ImagePixels source = TestImages.CreateGradient(64, 48);

        ImagePixels upscaled = LanczosResampler.Resize(source, 256, 192);

        Assert.Equal(256, upscaled.Width);
        Assert.Equal(192, upscaled.Height);
    }

    [Fact]
    public void Resize_SameSize_ReturnsSameInstance()
    {
        ImagePixels source = TestImages.CreateGradient(16, 16);
        Assert.Same(source, LanczosResampler.Resize(source, 16, 16));
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(4, 1)]
    [InlineData(3, 7)]
    public void Resize_ConstantImage_StaysConstant(int sourceSize, int targetSize)
    {
        var source = new ImagePixels(sourceSize, sourceSize);
        for (int i = 0; i < source.Pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            source.Pixels[i] = 128;
            source.Pixels[i + 1] = 128;
            source.Pixels[i + 2] = 128;
            source.Pixels[i + 3] = 255;
        }

        ImagePixels resized = LanczosResampler.Resize(source, targetSize, targetSize);

        for (int i = 0; i < resized.Pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            // 只检查颜色通道；alpha 在不透明常量图里应保持 255。
            Assert.InRange((int)resized.Pixels[i], 127, 129);
            Assert.InRange((int)resized.Pixels[i + 1], 127, 129);
            Assert.InRange((int)resized.Pixels[i + 2], 127, 129);
            Assert.Equal((byte)255, resized.Pixels[i + 3]);
        }
    }

    [Fact]
    public void Resize_PreservesCornerTrend()
    {
        var source = new ImagePixels(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int index = ((y * 8) + x) * ImagePixels.BytesPerPixel;
                source.Pixels[index] = (byte)(x * 32);
                source.Pixels[index + 1] = (byte)(y * 32);
                source.Pixels[index + 2] = 64;
                source.Pixels[index + 3] = 255;
            }
        }

        ImagePixels resized = LanczosResampler.Resize(source, 32, 32);

        // 左上角偏暗、右下角偏亮（趋势不应被振铃破坏）。
        int topLeft = resized.Pixels[0];
        int bottomRight = resized.Pixels[(31 * 32 * ImagePixels.BytesPerPixel) + (31 * ImagePixels.BytesPerPixel)];
        Assert.True(topLeft < 40, $"左上角应为暗部，实际 {topLeft}");
        Assert.True(bottomRight > 200, $"右下角应为亮部，实际 {bottomRight}");
    }

    [Fact]
    public void Resize_RejectsInvalidArguments()
    {
        ImagePixels source = TestImages.CreateGradient(8, 8);
        Assert.Throws<ArgumentNullException>(() => LanczosResampler.Resize(null!, 8, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => LanczosResampler.Resize(source, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => LanczosResampler.Resize(source, 8, -1));
    }
}
