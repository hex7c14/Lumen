using Lumen.Core.Imaging;

namespace Lumen.Core.Upscaling;

/// <summary>
/// 纯 C# 的 Lanczos3 重采样，用于 CLI quality 子命令的“传统插值基线”（也用于参考图 4 倍降采样）。
/// 物理意义与常见实现一致：放大时窗口 3 像素；缩小时按缩放比例展宽窗口（抗锯齿），窗口半径 = 3 / min(1, scale)。
/// 通道按非预乘 RGBA 独立处理，alpha 通道一起重采样。
/// </summary>
public static class LanczosResampler
{
    private const int A = 3;

    /// <summary>把图像重采样到目标尺寸。尺寸相同直接返回原对象。</summary>
    public static ImagePixels Resize(ImagePixels source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        if (source.Width == targetWidth && source.Height == targetHeight)
        {
            return source;
        }

        int srcWidth = source.Width;
        int srcHeight = source.Height;
        ResampleWeights horizontal = BuildWeights(srcWidth, targetWidth);
        ResampleWeights vertical = BuildWeights(srcHeight, targetHeight);

        byte[] src = source.Pixels;

        // 第一遍：水平方向，结果按 float 保存（逐行），内存约为 targetWidth * srcHeight * 4 通道。
        var intermediate = new float[(long)targetWidth * srcHeight * ImagePixels.BytesPerPixel];
        for (int y = 0; y < srcHeight; y++)
        {
            int sourceRow = y * srcWidth * ImagePixels.BytesPerPixel;
            int targetRow = y * targetWidth * ImagePixels.BytesPerPixel;
            for (int channel = 0; channel < 4; channel++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    int start = horizontal.Start[x];
                    int count = horizontal.Count[x];
                    double sum = 0;
                    for (int k = 0; k < count; k++)
                    {
                        sum += horizontal.Weights[horizontal.Offset[x] + k]
                               * src[sourceRow + ((start + k) * ImagePixels.BytesPerPixel) + channel];
                    }

                    intermediate[targetRow + (x * ImagePixels.BytesPerPixel) + channel] = (float)sum;
                }
            }
        }

        // 第二遍：垂直方向，结果四舍五入、钳制到 0..255。
        var result = new ImagePixels(targetWidth, targetHeight);
        byte[] dst = result.Pixels;
        for (int y = 0; y < targetHeight; y++)
        {
            int start = vertical.Start[y];
            int count = vertical.Count[y];
            int offset = vertical.Offset[y];
            int targetRow = y * targetWidth * ImagePixels.BytesPerPixel;
            for (int x = 0; x < targetWidth; x++)
            {
                int column = x * ImagePixels.BytesPerPixel;
                for (int channel = 0; channel < 4; channel++)
                {
                    double sum = 0;
                    for (int k = 0; k < count; k++)
                    {
                        int sourceRow = (start + k) * targetWidth * ImagePixels.BytesPerPixel;
                        sum += vertical.Weights[offset + k] * intermediate[sourceRow + column + channel];
                    }

                    int value = (int)Math.Round(sum);
                    if (value < 0)
                    {
                        value = 0;
                    }
                    else if (value > 255)
                    {
                        value = 255;
                    }

                    dst[targetRow + column + channel] = (byte)value;
                }
            }
        }

        return result;
    }

    private static ResampleWeights BuildWeights(int sourceSize, int targetSize)
    {
        var start = new int[targetSize];
        var count = new int[targetSize];
        var offset = new int[targetSize];
        var weights = new List<double>(targetSize * 8);

        double scale = (double)sourceSize / targetSize;
        double filterScale = Math.Min(1.0, 1.0 / scale);
        double support = A / filterScale;

        for (int i = 0; i < targetSize; i++)
        {
            // 目标像素中心映射回源坐标（像素中心对齐）。
            double center = ((i + 0.5) * scale) - 0.5;
            int left = (int)Math.Floor(center - support) + 1;
            int right = (int)Math.Ceiling(center + support) - 1;
            if (left < 0)
            {
                left = 0;
            }

            if (right > sourceSize - 1)
            {
                right = sourceSize - 1;
            }

            if (right < left)
            {
                right = left;
            }

            start[i] = left;
            count[i] = right - left + 1;
            offset[i] = weights.Count;
            double sum = 0;
            for (int s = left; s <= right; s++)
            {
                double weight = Lanczos3((s - center) * filterScale);
                weights.Add(weight);
                sum += weight;
            }

            if (Math.Abs(sum) < 1e-12)
            {
                // 极端情况下窗口内权重互相抵消；退化到最近邻，保证权重和为 1。
                for (int k = 0; k < count[i]; k++)
                {
                    weights[offset[i] + k] = 0;
                }

                weights[offset[i] + (count[i] / 2)] = 1;
            }
            else
            {
                for (int k = 0; k < count[i]; k++)
                {
                    weights[offset[i] + k] /= sum;
                }
            }
        }

        return new ResampleWeights(start, count, offset, weights.ToArray());
    }

    private static double Lanczos3(double x)
    {
        if (x == 0)
        {
            return 1;
        }

        if (x <= -A || x >= A)
        {
            return 0;
        }

        double px = Math.PI * x;
        return (A * Math.Sin(px) * Math.Sin(px / A)) / (px * px);
    }

    private sealed record ResampleWeights(int[] Start, int[] Count, int[] Offset, double[] Weights);
}
