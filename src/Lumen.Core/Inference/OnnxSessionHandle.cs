using Lumen.Core.Models;
using Lumen.Core.Upscaling;
using Microsoft.ML.OnnxRuntime;

namespace Lumen.Core.Inference;

/// <summary>
/// 已加载的 ONNX 会话及其输入/输出元数据。会话创建代价高（首次数秒），由
/// <see cref="Upscaling.OnnxUpscalerService"/> 按 (权重路径, 精度, 后端, 设备) 缓存复用。
/// 单会话推理由服务串行化，不在多个线程上并发调用。
/// </summary>
internal sealed class OnnxSessionHandle : IDisposable
{
    private int _disposed;

    public required InferenceSession Session { get; init; }

    public required string InputName { get; init; }

    public required string OutputName { get; init; }

    public required bool InputIsFloat16 { get; init; }

    public required bool OutputIsFloat16 { get; init; }

    public required ExecutionBackend EffectiveBackend { get; init; }

    public required ModelPrecision Precision { get; init; }

    public required string ModelPath { get; init; }

    /// <summary>写进 <see cref="Upscaling.UpscaleResult.Backend"/> 的字符串：DirectML 或 CPU。</summary>
    public required string BackendDisplayName { get; init; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Session.Dispose();
        }
    }
}
