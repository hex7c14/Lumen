using Lumen.Core.Imaging;

namespace Lumen.Core.Upscaling;

/// <summary>
/// 超分服务。实现负责：选择后端、加载 ONNX 会话、分块推理、拼接、目标倍率重采样、生成预览。
/// 所有耗时的同步工作都在调用线程上执行，调用方应放到后台线程（<see cref="Task.Run(Func{Task})"/> 或 Task 创建时已指定）。
/// </summary>
public interface IUpscalerService : IDisposable
{
    /// <summary>
    /// 对 <paramref name="input"/> 执行超分。进度通过 <paramref name="progress"/> 上报，
    /// 支持 <paramref name="cancellationToken"/> 取消（取消后抛出 <see cref="OperationCanceledException"/>）。
    /// </summary>
    Task<UpscaleResult> UpscaleAsync(
        LoadedImage input,
        UpscaleOptions options,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>释放已缓存的推理会话。</summary>
    void ClearSessionCache();
}
