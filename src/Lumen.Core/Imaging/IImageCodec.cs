namespace Lumen.Core.Imaging;

/// <summary>支持的输出容器格式。</summary>
public enum OutputFormat
{
    Png = 0,
    Jpeg = 1,
    WebP = 2,
    Bmp = 3,
}

/// <summary>重采样质量。</summary>
public enum ResampleMode
{
    /// <summary>高质量三次/米切尔滤波，用于预览与 2x/3x 输出。</summary>
    HighQuality = 0,

    /// <summary>最近邻，仅用于调试。</summary>
    Nearest = 1,
}

/// <summary>保存选项。</summary>
public sealed record SaveOptions(OutputFormat Format, int Quality = 92)
{
    public static SaveOptions Default { get; } = new(OutputFormat.Png);

    /// <summary>无损格式忽略质量参数；有损格式钳制到 [1, 100]。</summary>
    public int EffectiveQuality => Math.Clamp(Quality, 1, 100);
}

/// <summary>
/// 图像编解码与缩放。实现必须线程安全（可被后台线程并发调用）。
/// </summary>
public interface IImageCodec
{
    IReadOnlyCollection<string> SupportedDecodeExtensions { get; }

    /// <summary>解码文件；失败抛出 <see cref="InvalidDataException"/> 或 <see cref="IOException"/>。</summary>
    LoadedImage Load(string path);

    LoadedImage Load(Stream stream, string? hintName = null);

    /// <summary>把 RGBA 变化到目标尺寸，<see cref="ResampleMode.HighQuality"/> 使用米切尔三次采样。</summary>
    ImagePixels Resize(ImagePixels source, int targetWidth, int targetHeight, ResampleMode mode = ResampleMode.HighQuality);

    /// <summary>生成不超过给定边界的等比预览图；原图已足够小时返回原图引用。</summary>
    ImagePixels CreatePreview(ImagePixels source, int maxWidth, int maxHeight);

    /// <summary>编码保存。目标目录必须已存在。</summary>
    void Save(ImagePixels image, string path, SaveOptions options);
}
