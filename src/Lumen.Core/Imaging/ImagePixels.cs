namespace Lumen.Core.Imaging;

/// <summary>
/// 8 位 RGBA 图像，行优先、无行填充。通道顺序为 R、G、B、A。
/// Core 内部所有像素缓冲区都使用这一种格式，避免在不同库之间来回转换。
/// </summary>
public sealed class ImagePixels
{
    public const int BytesPerPixel = 4;

    public ImagePixels(int width, int height)
        : this(width, height, CreateBuffer(width, height))
    {
    }

    public ImagePixels(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        long expected = (long)width * height * BytesPerPixel;
        if (pixels.LongLength != expected)
        {
            throw new ArgumentException(
                $"像素缓冲区长度 {pixels.LongLength} 与 {width}x{height}x4（{expected} 字节）不一致。",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * BytesPerPixel;

    public int PixelCount => Width * Height;

    /// <summary>RGBA 像素，长度为 Width * Height * 4。</summary>
    public byte[] Pixels { get; }

    /// <summary>是否存在非不透明像素。用于决定是否需要对 alpha 通道做单独处理。</summary>
    public bool HasTransparency()
    {
        byte[] px = Pixels;
        for (int i = 3; i < px.Length; i += BytesPerPixel)
        {
            if (px[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    public ImagePixels Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>
    /// 复制为 WinUI <c>WriteableBitmap</c> 期望的 BGRA8 预乘格式。
    /// </summary>
    public void CopyToBgraPremultiplied(Span<byte> destination)
    {
        int expected = PixelCount * BytesPerPixel;
        if (destination.Length < expected)
        {
            throw new ArgumentException($"目标缓冲区至少需要 {expected} 字节，实际 {destination.Length}。", nameof(destination));
        }

        byte[] src = Pixels;
        for (int i = 0; i < src.Length; i += BytesPerPixel)
        {
            byte r = src[i];
            byte g = src[i + 1];
            byte b = src[i + 2];
            byte a = src[i + 3];

            if (a == 255)
            {
                destination[i] = b;
                destination[i + 1] = g;
                destination[i + 2] = r;
                destination[i + 3] = 255;
                continue;
            }

            // 预乘 alpha，四舍五入到最近整数。a == 0 时结果必须全 0。
            if (a == 0)
            {
                destination[i] = 0;
                destination[i + 1] = 0;
                destination[i + 2] = 0;
                destination[i + 3] = 0;
                continue;
            }

            destination[i] = (byte)((b * a + 127) / 255);
            destination[i + 1] = (byte)((g * a + 127) / 255);
            destination[i + 2] = (byte)((r * a + 127) / 255);
            destination[i + 3] = a;
        }
    }

    /// <summary>
    /// 从 BGRA（例如 WinUI Surface 或 GDI+ 读出的数据）转换为内部 RGBA 格式。
    /// </summary>
    public static ImagePixels FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int expected = width * height * BytesPerPixel;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"源缓冲区至少需要 {expected} 字节，实际 {bgra.Length}。", nameof(bgra));
        }

        var result = new ImagePixels(width, height);
        byte[] dst = result.Pixels;
        for (int i = 0; i < expected; i += BytesPerPixel)
        {
            dst[i] = bgra[i + 2];
            dst[i + 1] = bgra[i + 1];
            dst[i + 2] = bgra[i];
            dst[i + 3] = bgra[i + 3];
        }

        return result;
    }

    private static byte[] CreateBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        long bytes = (long)width * height * BytesPerPixel;
        if (bytes > int.MaxValue)
        {
            throw new OutOfMemoryException($"图像 {width}x{height} 的像素缓冲区（{bytes} 字节）超出单数组上限。");
        }

        return new byte[bytes];
    }
}
