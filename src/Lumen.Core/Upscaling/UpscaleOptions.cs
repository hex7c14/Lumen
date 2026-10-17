using Lumen.Core.Models;

namespace Lumen.Core.Upscaling;

/// <summary>推理后端偏好。</summary>
public enum ExecutionBackend
{
    /// <summary>有 DirectML 时用 GPU，否则 CPU。</summary>
    Auto = 0,

    DirectML = 1,

    Cpu = 2,
}

/// <summary>超分选项。</summary>
public sealed class UpscaleOptions
{
    /// <summary>使用的模型。</summary>
    public required ModelDescriptor Model { get; init; }

    /// <summary>目标倍数。模型本身固定倍数（通常 4），小于模型倍数时会在输出阶段做高质量降采样。</summary>
    public int TargetScale { get; init; } = 4;

    public ExecutionBackend Backend { get; init; } = ExecutionBackend.Auto;

    /// <summary>是否优先使用 fp16 权重（GPU 上更快，CPU 上通常更慢）。</summary>
    public bool PreferFp16 { get; init; } = true;

    /// <summary>分块边长（输入像素）。0 表示自动。</summary>
    public int TileSize { get; init; }

    /// <summary>分块重叠/裁剪边距（输入像素）。0 表示自动。</summary>
    public int TilePadding { get; init; }

    /// <summary>输出像素总数上限，用于防止大图把内存打爆。默认 4 亿像素。</summary>
    public long MaxOutputPixels { get; init; } = 400_000_000;

    /// <summary>保留 alpha 通道（透明图会单独放大 alpha）。</summary>
    public bool PreserveAlpha { get; init; } = true;

    /// <summary>结果预览图的长边上限；0 表示不生成预览。</summary>
    public int PreviewMaxDimension { get; init; } = 2560;

    /// <summary>GPU 设备序号（多 GPU 时使用）。</summary>
    public int GpuDeviceId { get; init; }

    /// <summary>CPU 推理线程数（IntraOp）；0 表示由 ONNX Runtime 自动决定。CPU 模式下可调。</summary>
    public int CpuThreads { get; init; }

    /// <summary>是否强制 CPU 兼容配置（由 UI 的“CPU 兼容模式”触发；引擎据此使用 fp32 与 CPU 友好分块）。</summary>
    public bool CpuCompatibilityMode { get; init; }

    /// <summary>校验倍率是否合法（1..model.Scale）。</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Model);
        if (Model.Scale <= 0)
        {
            throw new InvalidOperationException($"模型 {Model.Id} 的倍率无效：{Model.Scale}。");
        }

        if (TargetScale < 1 || TargetScale > Model.Scale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetScale),
                TargetScale,
                $"目标倍率必须在 1 到 {Model.Scale} 之间。");
        }

        if (TileSize is < 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(TileSize), TileSize, "分块边长必须在 0 到 4096 之间。");
        }

        if (TilePadding is < 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(TilePadding), TilePadding, "分块边距必须在 0 到 1024 之间。");
        }
    }
}
