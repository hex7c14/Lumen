using SkiaSharp;

namespace Lumen.Core.Imaging;

/// <summary>
/// 基于 Skia 的实现。负责解码、EXIF 方向归一化、高质量缩放、预览生成和编码保存。
/// 解码结果统一为 RGBA8 非预乘格式。
/// </summary>
public sealed class SkiaImageCodec : IImageCodec, IImageThumbnailLoader
{
    private static readonly string[] DecodeExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif", ".ico",
    ];

    public IReadOnlyCollection<string> SupportedDecodeExtensions => DecodeExtensions;

    public LoadedImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return LoadCore(stream, Path.GetFileName(path), path);
    }

    public LoadedImage Load(Stream stream, string? hintName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return LoadCore(stream, hintName, sourcePath: null);
    }

    /// <summary>
    /// 加载缩略图：优先让编码器按采样比例缩放解码（JPEG 支持 1/2、1/4、1/8），
    /// 避免为队列缩略图解码整张大图；不支持缩放解码的格式回退为全尺寸解码后再缩放。
    /// </summary>
    public ImagePixels LoadThumbnail(string path, int maxDimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);

        using FileStream stream = File.OpenRead(path);
        using SKData data = SKData.Create(stream)
            ?? throw new InvalidDataException($"无法读取图像数据：{path}");
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"不支持的图像格式：{path}");

        int sourceWidth = codec.Info.Width;
        int sourceHeight = codec.Info.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidDataException("图像尺寸无效。");
        }

        double desiredScale = Math.Min((double)maxDimension / Math.Max(sourceWidth, sourceHeight), 1.0);
        SKSizeI scaled = codec.GetScaledDimensions((float)desiredScale);
        int decodeWidth = scaled.Width > 0 ? Math.Min(scaled.Width, sourceWidth) : sourceWidth;
        int decodeHeight = scaled.Height > 0 ? Math.Min(scaled.Height, sourceHeight) : sourceHeight;

        ImagePixels pixels = DecodePixels(codec, decodeWidth, decodeHeight, path);
        SKEncodedOrigin origin = codec.EncodedOrigin;
        if (origin != SKEncodedOrigin.TopLeft)
        {
            pixels = ApplyOrientation(pixels, origin);
        }

        if (Math.Max(pixels.Width, pixels.Height) > maxDimension)
        {
            double fit = (double)maxDimension / Math.Max(pixels.Width, pixels.Height);
            pixels = Resize(
                pixels,
                Math.Max(1, (int)Math.Round(pixels.Width * fit)),
                Math.Max(1, (int)Math.Round(pixels.Height * fit)));
        }

        return pixels;
    }

    private static ImagePixels DecodePixels(SKCodec codec, int width, int height, string path)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"图像解码失败：{result}（{path}）。");
        }

        return FromBitmap(bitmap);
    }

    public ImagePixels Resize(ImagePixels source, int targetWidth, int targetHeight, ResampleMode mode = ResampleMode.HighQuality)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        if (targetWidth == source.Width && targetHeight == source.Height)
        {
            return source;
        }

        using SKBitmap sourceBitmap = ToBitmap(source);
        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKSamplingOptions sampling = mode == ResampleMode.Nearest
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            // Mitchell 三次滤波（B = C = 1/3）：缩放时比双线性更锐利、振铃比 Lanczos 更少。
            : new SKSamplingOptions(new SKCubicResampler(1f / 3f, 1f / 3f));

        using SKBitmap? resizedBitmap = sourceBitmap.Resize(info, sampling);
        if (resizedBitmap is null)
        {
            throw new InvalidOperationException($"图像缩放失败：{source.Width}x{source.Height} -> {targetWidth}x{targetHeight}。");
        }

        return FromBitmap(resizedBitmap);
    }

    public ImagePixels CreatePreview(ImagePixels source, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);

        if (source.Width <= maxWidth && source.Height <= maxHeight)
        {
            return source;
        }

        double scale = Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return Resize(source, width, height);
    }

    public void Save(ImagePixels image, string path, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (options.Format == OutputFormat.Bmp)
        {
            // Skia 4 只保留 BMP 解码，没有编码器；这里手写 24 位 BMP（无 alpha，透明图合成白底）。
            ImagePixels bmpPayload = image.HasTransparency() ? FlattenOntoWhite(image) : image;
            WriteBmp24(bmpPayload, path);
            return;
        }

        SKEncodedImageFormat format = options.Format switch
        {
            OutputFormat.Png => SKEncodedImageFormat.Png,
            OutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            OutputFormat.WebP => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Format, "不支持的输出格式。"),
        };

        ImagePixels payload = options.Format == OutputFormat.Jpeg && image.HasTransparency()
            ? FlattenOntoWhite(image)
            : image;

        using SKBitmap bitmap = ToBitmap(payload);
        using SKImage bitmapImage = SKImage.FromBitmap(bitmap);
        using SKData? encoded = bitmapImage.Encode(format, options.EffectiveQuality)
            ?? throw new InvalidOperationException($"编码 {options.Format} 失败。");

        using FileStream stream = File.Create(path);
        encoded.SaveTo(stream);
    }

    /// <summary>
    /// 写 24 位无压缩 BMP（BI_RGB，自底向上）。BMP 不承载 alpha，调用方负责先合成白底。
    /// </summary>
    private static void WriteBmp24(ImagePixels image, string path)
    {
        int width = image.Width;
        int height = image.Height;
        int rowStride = ((width * 3) + 3) & ~3;
        long imageSize = (long)rowStride * height;
        long fileSize = 54 + imageSize;
        if (fileSize > int.MaxValue)
        {
            throw new InvalidOperationException($"BMP 文件过大（{fileSize:N0} 字节），请改用 PNG/TIFF 等格式。");
        }

        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write((int)fileSize);
        writer.Write(0);
        writer.Write(54);

        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);              // BI_RGB
        writer.Write((int)imageSize);
        writer.Write(2835);           // 72 DPI
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        byte[] row = new byte[rowStride];
        byte[] source = image.Pixels;
        for (int y = height - 1; y >= 0; y--)
        {
            int sourceIndex = y * width * ImagePixels.BytesPerPixel;
            int destinationIndex = 0;
            for (int x = 0; x < width; x++)
            {
                row[destinationIndex] = source[sourceIndex + 2];
                row[destinationIndex + 1] = source[sourceIndex + 1];
                row[destinationIndex + 2] = source[sourceIndex];
                sourceIndex += ImagePixels.BytesPerPixel;
                destinationIndex += 3;
            }

            for (int padding = destinationIndex; padding < rowStride; padding++)
            {
                row[padding] = 0;
            }

            writer.Write(row);
        }
    }

    private static LoadedImage LoadCore(Stream stream, string? hintName, string? sourcePath)
    {
        using SKData data = SKData.Create(stream)
            ?? throw new InvalidDataException($"无法读取图像数据（{hintName ?? "流"}）。");
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException($"不支持的图像格式：{hintName ?? "未知"}。");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new InvalidDataException("图像尺寸无效。");
        }

        using var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"图像解码失败：{result}（{hintName ?? "流"}）。");
        }

        ImagePixels pixels = FromBitmap(bitmap);
        SKEncodedOrigin origin = codec.EncodedOrigin;
        if (origin != SKEncodedOrigin.TopLeft)
        {
            pixels = ApplyOrientation(pixels, origin);
        }

        // SkiaSharp 4 不直接暴露 ICC 配置文件；当前不做色彩空间转换，先记录为 null。
        return new LoadedImage(
            pixels,
            sourcePath,
            codec.EncodedFormat.ToString().ToUpperInvariant(),
            colorProfile: null,
            origin.ToString());
    }

    /// <summary>按 EXIF 方向把像素转正。5-8 会交换宽高。</summary>
    private static ImagePixels ApplyOrientation(ImagePixels source, SKEncodedOrigin origin)
    {
        int w = source.Width;
        int h = source.Height;
        bool swapped = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int destWidth = swapped ? h : w;
        int destHeight = swapped ? w : h;

        var dest = new ImagePixels(destWidth, destHeight);
        byte[] src = source.Pixels;
        byte[] dst = dest.Pixels;

        for (int dy = 0; dy < destHeight; dy++)
        {
            for (int dx = 0; dx < destWidth; dx++)
            {
                (int sx, int sy) = origin switch
                {
                    SKEncodedOrigin.TopLeft => (dx, dy),
                    SKEncodedOrigin.TopRight => (w - 1 - dx, dy),
                    SKEncodedOrigin.BottomRight => (w - 1 - dx, h - 1 - dy),
                    SKEncodedOrigin.BottomLeft => (dx, h - 1 - dy),
                    SKEncodedOrigin.LeftTop => (dy, dx),
                    SKEncodedOrigin.RightTop => (dy, h - 1 - dx),
                    SKEncodedOrigin.RightBottom => (w - 1 - dy, h - 1 - dx),
                    SKEncodedOrigin.LeftBottom => (w - 1 - dy, dx),
                    _ => (dx, dy),
                };

                int si = ((sy * w) + sx) * ImagePixels.BytesPerPixel;
                int di = ((dy * destWidth) + dx) * ImagePixels.BytesPerPixel;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dest;
    }

    private static SKBitmap ToBitmap(ImagePixels image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        image.Pixels.AsSpan().CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    private static ImagePixels FromBitmap(SKBitmap bitmap)
    {
        var result = new ImagePixels(bitmap.Width, bitmap.Height);
        Span<byte> span = result.Pixels;
        bitmap.GetPixelSpan().Slice(0, span.Length).CopyTo(span);
        return result;
    }

    private static ImagePixels FlattenOntoWhite(ImagePixels source)
    {
        var result = new ImagePixels(source.Width, source.Height);
        byte[] src = source.Pixels;
        byte[] dst = result.Pixels;
        for (int i = 0; i < src.Length; i += ImagePixels.BytesPerPixel)
        {
            int a = src[i + 3];
            if (a == 255)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i + 2];
                dst[i + 3] = 255;
                continue;
            }

            // over white: out = src * a + 255 * (1 - a)
            int inverse = 255 - a;
            dst[i] = (byte)((src[i] * a + 255 * inverse + 127) / 255);
            dst[i + 1] = (byte)((src[i + 1] * a + 255 * inverse + 127) / 255);
            dst[i + 2] = (byte)((src[i + 2] * a + 255 * inverse + 127) / 255);
            dst[i + 3] = 255;
        }

        return result;
    }
}
