using System.Buffers;
using Lumen.Core.Imaging;
using Lumen.Core.Upscaling;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lumen.Core.Inference;

/// <summary>
/// 单块推理：从 RGBA 输入里取出“核心 + 上下文”区域 → NCHW RGB [0,1] 张量（按模型精度选择 float / float16）
/// → 推理 → 把输出张量裁掉上下文边距，写回 RGBA 输出缓冲的 RGB 通道（alpha 由服务层统一处理）。
/// 输出张量必须在 ORT 结果对象释放前读完（DirectML 输出可能直接引用 EP 内存）。
/// </summary>
internal static class TileInference
{
    /// <summary>
    /// 运行一个块。输出写入 <paramref name="output"/> 的 (tile.X*scale, tile.Y*scale) 处。
    /// <paramref name="premultiplyAlpha"/> 为 true 时输入 RGB 先乘 alpha（透明图先消除垃圾 RGB），
    /// 输出 RGB 也是预乘值，服务层随后按重采样后的 alpha 反预乘。
    /// <paramref name="uniformTileShape"/> 为 true 时，所有块的张量尺寸固定为
    /// (tile.Height+2*padY) x (tile.Width+2*padX)，图像边界外按反射 101 填充，使 DirectML 始终看到同一形状。
    /// </summary>
    internal static void Run(
        OnnxSessionHandle handle,
        ImagePixels input,
        ImagePixels output,
        TileRect tile,
        int paddingX,
        int paddingY,
        int scale,
        bool premultiplyAlpha,
        bool uniformTileShape)
    {
        int sourceX;
        int sourceY;
        int sourceWidth;
        int sourceHeight;
        if (uniformTileShape)
        {
            sourceX = tile.X - paddingX;
            sourceY = tile.Y - paddingY;
            sourceWidth = tile.Width + (paddingX * 2);
            sourceHeight = tile.Height + (paddingY * 2);
        }
        else
        {
            sourceX = Math.Max(0, tile.X - paddingX);
            sourceY = Math.Max(0, tile.Y - paddingY);
            sourceWidth = Math.Min(input.Width, tile.Right + paddingX) - sourceX;
            sourceHeight = Math.Min(input.Height, tile.Bottom + paddingY) - sourceY;
        }

        int expectedOutputWidth = sourceWidth * scale;
        int expectedOutputHeight = sourceHeight * scale;

        int pixelCount = sourceWidth * sourceHeight;
        int inputLength = pixelCount * 3;
        float[] rgb = ArrayPool<float>.Shared.Rent(inputLength);
        try
        {
            FillRgbTensorSource(
                input,
                rgb,
                sourceX,
                sourceY,
                sourceWidth,
                sourceHeight,
                premultiplyAlpha,
                uniformTileShape);

            // 写入回调在 ORT 结果对象生命周期内执行，避免读取已释放的 EP 内存。
            if (handle.InputIsFloat16)
            {
                Float16[] half = ArrayPool<Float16>.Shared.Rent(inputLength);
                try
                {
                    for (int i = 0; i < inputLength; i++)
                    {
                        half[i] = (Float16)rgb[i];
                    }

                    var tensor = new DenseTensor<Float16>(half.AsMemory(0, inputLength), [1, 3, sourceHeight, sourceWidth]);
                    if (handle.OutputIsFloat16)
                    {
                        RunAndConsume(handle, tensor, (Tensor<Float16> values) =>
                        {
                            ValidateOutputDimensions(values.Dimensions, handle, expectedOutputWidth, expectedOutputHeight);
                            WriteHalfOutput(values, output, tile, sourceX, sourceY, scale);
                        });
                    }
                    else
                    {
                        RunAndConsume(handle, tensor, (Tensor<float> values) =>
                        {
                            ValidateOutputDimensions(values.Dimensions, handle, expectedOutputWidth, expectedOutputHeight);
                            WriteFloatOutput(values, output, tile, sourceX, sourceY, scale);
                        });
                    }
                }
                finally
                {
                    ArrayPool<Float16>.Shared.Return(half);
                }
            }
            else
            {
                var tensor = new DenseTensor<float>(rgb.AsMemory(0, inputLength), [1, 3, sourceHeight, sourceWidth]);
                if (handle.OutputIsFloat16)
                {
                    RunAndConsume(handle, tensor, (Tensor<Float16> values) =>
                    {
                        ValidateOutputDimensions(values.Dimensions, handle, expectedOutputWidth, expectedOutputHeight);
                        WriteHalfOutput(values, output, tile, sourceX, sourceY, scale);
                    });
                }
                else
                {
                    RunAndConsume(handle, tensor, (Tensor<float> values) =>
                    {
                        ValidateOutputDimensions(values.Dimensions, handle, expectedOutputWidth, expectedOutputHeight);
                        WriteFloatOutput(values, output, tile, sourceX, sourceY, scale);
                    });
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rgb);
        }
    }

    private static void RunAndConsume<TIn, TOut>(
        OnnxSessionHandle handle,
        DenseTensor<TIn> input,
        Action<Tensor<TOut>> consume)
        where TIn : struct
        where TOut : struct
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            handle.Session.Run([NamedOnnxValue.CreateFromTensor(handle.InputName, input)]);
        consume(results[0].AsTensor<TOut>());
    }

    /// <summary>RGBA → NCHW RGB，值域 [0,1]（直接 /255，不做 gamma 转换）。</summary>
    private static void FillRgbTensorSource(
        ImagePixels input,
        float[] destination,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        bool premultiplyAlpha,
        bool reflectOutOfBounds)
    {
        const float ToUnit = 1f / 255f;
        byte[] pixels = input.Pixels;
        int inputWidth = input.Width;
        int inputHeight = input.Height;
        int plane = sourceWidth * sourceHeight;
        int destinationIndex = 0;
        for (int y = 0; y < sourceHeight; y++)
        {
            int globalY = sourceY + y;
            int row = (reflectOutOfBounds ? Reflect101(globalY, inputHeight) : globalY) * inputWidth;
            for (int x = 0; x < sourceWidth; x++)
            {
                int globalX = sourceX + x;
                int column = reflectOutOfBounds ? Reflect101(globalX, inputWidth) : globalX;
                int index = (row + column) * ImagePixels.BytesPerPixel;
                float alpha = premultiplyAlpha ? pixels[index + 3] * ToUnit : 1f;
                destination[destinationIndex] = pixels[index] * ToUnit * alpha;
                destination[plane + destinationIndex] = pixels[index + 1] * ToUnit * alpha;
                destination[(plane * 2) + destinationIndex] = pixels[index + 2] * ToUnit * alpha;
                destinationIndex++;
            }
        }
    }

    /// <summary>反射 101（不含边缘重复）：-1 → 1，length → length-2；支持任意越界距离。</summary>
    private static int Reflect101(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        int period = 2 * (length - 1);
        index %= period;
        if (index < 0)
        {
            index += period;
        }

        return index < length ? index : period - index;
    }

    // ---- 输出裁剪、钳制、写回 ----
    // 注意：这里刻意不抽泛型 + 委托：4096² 输出有约 5000 万个通道值，每通道一次委托调用是
    // 可测量的 CPU 热点。float / Half 两条路径各自专用内联，数值语义与旧实现完全一致
    // （[0,1] 钳制 + 四舍五入 + NaN→0，见 ToByte）。

    private static void WriteFloatOutput(
        Tensor<float> tensor,
        ImagePixels output,
        TileRect tile,
        int sourceX,
        int sourceY,
        int scale)
    {
        int tensorWidth = tensor.Dimensions[3];
        int plane = tensor.Dimensions[2] * tensorWidth;
        ReadOnlySpan<float> values = tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray();
        WriteCropFloat(values, plane, tensorWidth, output, tile, sourceX, sourceY, scale);
    }

    private static void WriteHalfOutput(
        Tensor<Float16> tensor,
        ImagePixels output,
        TileRect tile,
        int sourceX,
        int sourceY,
        int scale)
    {
        int tensorWidth = tensor.Dimensions[3];
        int plane = tensor.Dimensions[2] * tensorWidth;
        if (tensor is DenseTensor<Float16> dense)
        {
            ReadOnlySpan<Float16> values = dense.Buffer.Span;
            WriteCropHalf(values, plane, tensorWidth, output, tile, sourceX, sourceY, scale);
        }
        else
        {
            Float16[] values = tensor.ToArray();
            WriteCropHalf(values, plane, tensorWidth, output, tile, sourceX, sourceY, scale);
        }
    }

    private static void WriteCropFloat(
        ReadOnlySpan<float> values,
        int plane,
        int tensorWidth,
        ImagePixels output,
        TileRect tile,
        int sourceX,
        int sourceY,
        int scale)
    {
        int cropX = (tile.X - sourceX) * scale;
        int cropY = (tile.Y - sourceY) * scale;
        int cropRight = cropX + (tile.Width * scale);
        int cropBottom = cropY + (tile.Height * scale);
        int destinationX = tile.X * scale;
        int destinationY = tile.Y * scale;
        byte[] destination = output.Pixels;
        int outputWidth = output.Width;
        int plane2 = plane * 2;

        for (int y = cropY; y < cropBottom; y++)
        {
            int rowBase = y * tensorWidth;
            int destinationRow = ((destinationY + (y - cropY)) * outputWidth) + destinationX;
            int di = destinationRow * ImagePixels.BytesPerPixel;
            int index = rowBase + cropX;
            for (int x = cropRight - cropX; x > 0; x--)
            {
                destination[di] = ToByte(values[index]);
                destination[di + 1] = ToByte(values[plane + index]);
                destination[di + 2] = ToByte(values[plane2 + index]);
                di += ImagePixels.BytesPerPixel;
                index++;
            }
        }
    }

    private static void WriteCropHalf(
        ReadOnlySpan<Float16> values,
        int plane,
        int tensorWidth,
        ImagePixels output,
        TileRect tile,
        int sourceX,
        int sourceY,
        int scale)
    {
        int cropX = (tile.X - sourceX) * scale;
        int cropY = (tile.Y - sourceY) * scale;
        int cropRight = cropX + (tile.Width * scale);
        int cropBottom = cropY + (tile.Height * scale);
        int destinationX = tile.X * scale;
        int destinationY = tile.Y * scale;
        byte[] destination = output.Pixels;
        int outputWidth = output.Width;
        int plane2 = plane * 2;

        for (int y = cropY; y < cropBottom; y++)
        {
            int rowBase = y * tensorWidth;
            int destinationRow = ((destinationY + (y - cropY)) * outputWidth) + destinationX;
            int di = destinationRow * ImagePixels.BytesPerPixel;
            int index = rowBase + cropX;
            for (int x = cropRight - cropX; x > 0; x--)
            {
                destination[di] = ToByte((float)values[index]);
                destination[di + 1] = ToByte((float)values[plane + index]);
                destination[di + 2] = ToByte((float)values[plane2 + index]);
                di += ImagePixels.BytesPerPixel;
                index++;
            }
        }
    }

    private static void ValidateOutputDimensions(
        ReadOnlySpan<int> dimensions,
        OnnxSessionHandle handle,
        int expectedWidth,
        int expectedHeight)
    {
        if (dimensions.Length != 4 || dimensions[2] != expectedHeight || dimensions[3] != expectedWidth)
        {
            string actual = dimensions.Length == 0 ? "空" : string.Join("x", dimensions.ToArray());
            throw new InvalidOperationException(
                $"模型输出尺寸 [{actual}] 与预期 [1,3,{expectedHeight},{expectedWidth}] 不一致；" +
                $"请检查 model.json 的 scale 与实际倍率是否一致（{handle.ModelPath}）。");
        }
    }

    /// <summary>[0,1] → 0..255，钳制并四舍五入；NaN 视为 0。</summary>
    private static byte ToByte(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        if (value <= 0f)
        {
            return 0;
        }

        if (value >= 1f)
        {
            return 255;
        }

        return (byte)((value * 255f) + 0.5f);
    }
}
