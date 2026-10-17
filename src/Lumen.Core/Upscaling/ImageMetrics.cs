namespace Lumen.Core.Upscaling;

/// <summary>
/// 图像质量指标（PSNR / SSIM），用于 CLI 的 quality 子命令与回归对比。
/// 计算在 RGB（0..255）上进行；SSIM 使用标准的高斯窗（11x11，σ=1.5）作用于亮度通道。
/// </summary>
public static class ImageMetrics
{
    private const double LumaRed = 0.2126;
    private const double LumaGreen = 0.7152;
    private const double LumaBlue = 0.0722;

    // 标准 SSIM 常数：K1=0.01, K2=0.03, L=255。
    private const double C1 = 6.5025;
    private const double C2 = 58.5225;

    /// <summary>RGB 通道的平均均方误差（像素值 0..255）。</summary>
    public static double MeanSquaredError(Imaging.ImagePixels expected, Imaging.ImagePixels actual)
    {
        ValidatePair(expected, actual);

        byte[] a = expected.Pixels;
        byte[] b = actual.Pixels;
        double sum = 0;
        for (int i = 0; i < a.Length; i += Imaging.ImagePixels.BytesPerPixel)
        {
            double dr = a[i] - b[i];
            double dg = a[i + 1] - b[i + 1];
            double db = a[i + 2] - b[i + 2];
            sum += (dr * dr) + (dg * dg) + (db * db);
        }

        return sum / ((long)expected.PixelCount * 3);
    }

    /// <summary>
    /// 峰值信噪比（dB）。完全一致时返回 <see cref="double.PositiveInfinity"/>。
    /// </summary>
    public static double Psnr(Imaging.ImagePixels expected, Imaging.ImagePixels actual)
    {
        double mse = MeanSquaredError(expected, actual);
        if (mse <= 0)
        {
            return double.PositiveInfinity;
        }

        return 10.0 * Math.Log10((255.0 * 255.0) / mse);
    }

    /// <summary>
    /// 结构相似度（0..1）。按亮度通道计算，窗口 11x11、σ=1.5，边界窗口按有效像素数加权。
    /// </summary>
    public static double Ssim(Imaging.ImagePixels expected, Imaging.ImagePixels actual)
    {
        ValidatePair(expected, actual);

        int width = expected.Width;
        int height = expected.Height;
        double[] reference = Luma(expected);
        double[] candidate = Luma(actual);

        const int radius = 5;
        const int window = (radius * 2) + 1;
        double[] kernel = GaussianKernel(window, 1.5);

        double ssimSum = 0;
        double weightSum = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(width - 1, x + radius);
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(height - 1, y + radius);

                double mu1 = 0;
                double mu2 = 0;
                double weight = 0;
                for (int wy = y0; wy <= y1; wy++)
                {
                    // 相对窗口中心的偏移：windowY ∈ [0, 2*radius]。
                    double ky = kernel[wy - y + radius];
                    int row = wy * width;
                    for (int wx = x0; wx <= x1; wx++)
                    {
                        double k = ky * kernel[wx - x + radius];
                        mu1 += k * reference[row + wx];
                        mu2 += k * candidate[row + wx];
                        weight += k;
                    }
                }

                mu1 /= weight;
                mu2 /= weight;

                double sigma1Sq = 0;
                double sigma2Sq = 0;
                double covariance = 0;
                for (int wy = y0; wy <= y1; wy++)
                {
                    double ky = kernel[wy - y + radius];
                    int row = wy * width;
                    for (int wx = x0; wx <= x1; wx++)
                    {
                        double k = ky * kernel[wx - x + radius];
                        double d1 = reference[row + wx] - mu1;
                        double d2 = candidate[row + wx] - mu2;
                        sigma1Sq += k * d1 * d1;
                        sigma2Sq += k * d2 * d2;
                        covariance += k * d1 * d2;
                    }
                }

                sigma1Sq /= weight;
                sigma2Sq /= weight;
                covariance /= weight;

                double numerator = ((2 * mu1 * mu2) + C1) * ((2 * covariance) + C2);
                double denominator = ((mu1 * mu1) + (mu2 * mu2) + C1) * (sigma1Sq + sigma2Sq + C2);
                double value = denominator == 0 ? 1.0 : numerator / denominator;

                ssimSum += value;
                weightSum += 1;
            }
        }

        return weightSum == 0 ? 0 : ssimSum / weightSum;
    }

    private static void ValidatePair(Imaging.ImagePixels expected, Imaging.ImagePixels actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            throw new ArgumentException(
                $"两张图尺寸不同：{expected.Width}x{expected.Height} vs {actual.Width}x{actual.Height}。",
                nameof(actual));
        }
    }

    /// <summary>亮度通道（Rec.709 权重），0..255。</summary>
    private static double[] Luma(Imaging.ImagePixels pixels)
    {
        byte[] source = pixels.Pixels;
        var luma = new double[pixels.PixelCount];
        for (int i = 0, j = 0; i < source.Length; i += Imaging.ImagePixels.BytesPerPixel, j++)
        {
            luma[j] = (LumaRed * source[i]) + (LumaGreen * source[i + 1]) + (LumaBlue * source[i + 2]);
        }

        return luma;
    }

    private static double[] GaussianKernel(int length, double sigma)
    {
        var kernel = new double[length];
        int center = length / 2;
        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            double d = i - center;
            kernel[i] = Math.Exp(-(d * d) / (2 * sigma * sigma));
            sum += kernel[i];
        }

        for (int i = 0; i < length; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }
}
