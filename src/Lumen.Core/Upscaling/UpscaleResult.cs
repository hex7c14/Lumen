using Lumen.Core.Imaging;
using Lumen.Core.Models;

namespace Lumen.Core.Upscaling;

/// <summary>一次超分的结果。</summary>
public sealed class UpscaleResult
{
    /// <summary>完整尺寸输出（RGBA8）。</summary>
    public required ImagePixels Output { get; init; }

    /// <summary>用于界面显示的预览图；未请求预览时为 null。</summary>
    public ImagePixels? Preview { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>实际使用的后端说明，例如 “DirectML” 或 “CPU”。</summary>
    public required string Backend { get; init; }

    public required string ModelId { get; init; }

    public required ModelPrecision Precision { get; init; }

    public int TileSize { get; init; }

    public int TileCount { get; init; }

    public int OutputWidth => Output.Width;

    public int OutputHeight => Output.Height;
}
