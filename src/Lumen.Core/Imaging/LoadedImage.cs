namespace Lumen.Core.Imaging;

/// <summary>
/// 解码后的输入图像。EXIF 方向已在解码阶段归一化，下游无需再关心。
/// </summary>
public sealed class LoadedImage
{
    private bool? _hasTransparency;

    public LoadedImage(
        ImagePixels pixels,
        string? sourcePath = null,
        string? sourceFormat = null,
        string? colorProfile = null,
        string? originalOrientation = null)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        SourcePath = sourcePath;
        SourceFormat = sourceFormat;
        ColorProfile = colorProfile;
        OriginalOrientation = originalOrientation;
    }

    public ImagePixels Pixels { get; }

    public int Width => Pixels.Width;

    public int Height => Pixels.Height;

    /// <summary>源文件路径；从流/剪贴板载入时为 null。</summary>
    public string? SourcePath { get; }

    /// <summary>源文件容器格式，例如 PNG、JPEG、WEBP。</summary>
    public string? SourceFormat { get; }

    /// <summary>内嵌 ICC 配置文件的描述；无则为 null。当前不做色彩空间转换，仅记录。</summary>
    public string? ColorProfile { get; }

    /// <summary>解码时应用过的 EXIF 方向名（TopLeft 表示无需旋转）。</summary>
    public string? OriginalOrientation { get; }

    public bool HasTransparency => _hasTransparency ??= Pixels.HasTransparency();
}
