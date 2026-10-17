using Lumen.Core.Imaging;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Tests;

public sealed class ImageMetricsTests
{
    [Fact]
    public void Psnr_IdenticalImages_IsPositiveInfinity()
    {
        ImagePixels image = TestImages.CreateGradient(32, 24);

        Assert.Equal(double.PositiveInfinity, ImageMetrics.Psnr(image, image.Clone()));
        Assert.Equal(0.0, ImageMetrics.MeanSquaredError(image, image.Clone()));
    }

    [Fact]
    public void Psnr_ConstantOffset_MatchesFormula()
    {
        var expected = new ImagePixels(4, 4);
        var actual = new ImagePixels(4, 4);
        for (int i = 0; i < expected.Pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            expected.Pixels[i] = 100;
            expected.Pixels[i + 1] = 100;
            expected.Pixels[i + 2] = 100;
            expected.Pixels[i + 3] = 255;
            actual.Pixels[i] = 110;
            actual.Pixels[i + 1] = 110;
            actual.Pixels[i + 2] = 110;
            actual.Pixels[i + 3] = 255;
        }

        double psnr = ImageMetrics.Psnr(expected, actual);
        double expectedPsnr = 20 * Math.Log10(255.0 / 10.0);
        Assert.Equal(expectedPsnr, psnr, 6);
    }

    [Fact]
    public void Ssim_IdenticalImages_IsOne()
    {
        ImagePixels image = TestImages.CreateGradient(48, 32);
        Assert.Equal(1.0, ImageMetrics.Ssim(image, image.Clone()), 6);
    }

    [Fact]
    public void Ssim_DegradesWithNoise()
    {
        ImagePixels reference = TestImages.CreateGradient(64, 48);
        ImagePixels noisy = reference.Clone();
        var random = new Random(42);
        for (int i = 0; i < noisy.Pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            noisy.Pixels[i] = (byte)Math.Clamp(noisy.Pixels[i] + random.Next(-40, 41), 0, 255);
            noisy.Pixels[i + 1] = (byte)Math.Clamp(noisy.Pixels[i + 1] + random.Next(-40, 41), 0, 255);
        }

        double ssim = ImageMetrics.Ssim(reference, noisy);
        Assert.True(ssim < 0.999, $"加噪后 SSIM 应低于 1，实际 {ssim}");
        Assert.True(ssim > 0.2, $"SSIM 过低：{ssim}");
    }

    [Fact]
    public void Metrics_DimensionMismatch_Throws()
    {
        ImagePixels a = TestImages.CreateGradient(8, 8);
        ImagePixels b = TestImages.CreateGradient(8, 9);

        Assert.Throws<ArgumentException>(() => ImageMetrics.Psnr(a, b));
        Assert.Throws<ArgumentException>(() => ImageMetrics.Ssim(a, b));
    }
}
