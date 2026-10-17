using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Lumen.Core.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lumen.App.Services;

/// <summary>
/// 把 Core 的 RGBA8 像素缓冲转成 WinUI 可渲染的 <see cref="WriteableBitmap"/>。
///
/// 迭代 3 起拆成两步：
/// 1. <see cref="PrepareBgraPremultiplied"/> 可以在**后台线程**做 RGBA→BGRA 预乘转换（大图可达十几 MB / 数百万像素循环）；
/// 2. <see cref="CreateBitmap(int, int, byte[])"/> 只在 UI 线程写 PixelBuffer（WinUI 对象有线程亲和性）。
/// 这样画布/对比预览的像素转换不再占用 UI 线程。
/// </summary>
public static class ImageDisplayService
{
    /// <summary>在后台线程把 RGBA 转成 WriteableBitmap 需要的 BGRA 预乘缓冲。</summary>
    public static byte[] PrepareBgraPremultiplied(ImagePixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        byte[] buffer = new byte[pixels.PixelCount * ImagePixels.BytesPerPixel];
        pixels.CopyToBgraPremultiplied(buffer);
        return buffer;
    }

    /// <summary>UI 线程：把已经转换好的 BGRA 预乘缓冲写入 WriteableBitmap。</summary>
    public static WriteableBitmap CreateBitmap(int width, int height, byte[] bgraPremultiplied)
    {
        ArgumentNullException.ThrowIfNull(bgraPremultiplied);

        int expected = width * height * ImagePixels.BytesPerPixel;
        if (bgraPremultiplied.Length < expected)
        {
            throw new ArgumentException($"BGRA 缓冲区至少需要 {expected} 字节，实际 {bgraPremultiplied.Length}。", nameof(bgraPremultiplied));
        }

        var bitmap = new WriteableBitmap(width, height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgraPremultiplied, 0, expected);
        }

        return bitmap;
    }

    /// <summary>便捷方法（小图/缩略图用）：转换 + 创建都在调用线程完成。</summary>
    public static WriteableBitmap CreateBitmap(ImagePixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        return CreateBitmap(pixels.Width, pixels.Height, PrepareBgraPremultiplied(pixels));
    }
}
