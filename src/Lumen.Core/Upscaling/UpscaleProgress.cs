namespace Lumen.Core.Upscaling;

/// <summary>超分阶段，UI 用来说明当前在做什么。</summary>
public enum UpscaleStage
{
    Preparing = 0,
    Inference = 1,
    PostProcessing = 2,
    Completed = 3,
}

/// <summary>进度快照。</summary>
public readonly record struct UpscaleProgress
{
    /// <summary>0..1 的整体进度。</summary>
    public required double Fraction { get; init; }

    public required UpscaleStage Stage { get; init; }

    public int TilesCompleted { get; init; }

    public int TilesTotal { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>可选的补充说明（例如“正在加载模型”）。</summary>
    public string? Message { get; init; }
}
