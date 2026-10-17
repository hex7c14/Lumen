using Lumen.Core.Imaging;
using Lumen.Core.Models;

namespace Lumen.Core.Batch;

/// <summary>批处理中单张图片的结果。</summary>
public sealed class BatchItemResult
{
    public required string InputPath { get; init; }

    /// <summary>
    /// 输出路径。语义：<see cref="BatchItemStatus.Completed"/> = 实际写出的文件；
    /// <see cref="BatchItemStatus.Skipped"/> = 本应写出的计划路径（文件未写入）；
    /// Failed / Canceled = null。
    /// </summary>
    public string? OutputPath { get; init; }

    public required BatchItemStatus Status { get; init; }

    /// <summary>失败原因（中文，可直接显示）。</summary>
    public string? ErrorMessage { get; init; }

    public int InputWidth { get; init; }

    public int InputHeight { get; init; }

    public int OutputWidth { get; init; }

    public int OutputHeight { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>结果预览（<see cref="BatchOptions.KeepPreview"/> 为 true 时生成）。</summary>
    public ImagePixels? Preview { get; init; }

    public string? Backend { get; init; }

    public ModelPrecision Precision { get; init; }
}
