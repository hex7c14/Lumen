namespace Lumen.Core.Batch;

/// <summary>批处理进度快照。UI 用它更新队列项与整体进度条。</summary>
public readonly record struct BatchProgress
{
    /// <summary>当前项下标（0 基）。</summary>
    public required int ItemIndex { get; init; }

    public required int ItemCount { get; init; }

    public required string InputPath { get; init; }

    public required BatchItemStage Stage { get; init; }

    /// <summary>整体进度 0..1（含已完成项与当前项内部进度）。</summary>
    public double OverallFraction { get; init; }

    /// <summary>当前项内部进度 0..1。</summary>
    public double ItemFraction { get; init; }

    public int TilesCompleted { get; init; }

    public int TilesTotal { get; init; }

    public string? OutputPath { get; init; }

    public string? Message { get; init; }

    public TimeSpan Elapsed { get; init; }
}
