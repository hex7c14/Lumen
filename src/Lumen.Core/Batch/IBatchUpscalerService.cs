namespace Lumen.Core.Batch;

/// <summary>
/// 批量超分服务：顺序处理多张图片（逐张解码 → 超分 → 保存），单张失败不中断整批，
/// 支持取消与整体/单项进度。实现必须复用同一个 <see cref="Upscaling.IUpscalerService"/>，
/// 以便 ONNX 会话在图片之间复用。
/// </summary>
public interface IBatchUpscalerService
{
    /// <summary>
    /// 处理一批输入文件。返回与输入一一对应的结果（顺序一致）。
    /// 单张解码/推理/保存失败会记录为 <see cref="BatchItemStatus.Failed"/> 并继续下一张；
    /// 取消时当前项及后续项标记为 <see cref="BatchItemStatus.Canceled"/>（不会抛出，除非参数非法）。
    /// </summary>
    Task<IReadOnlyList<BatchItemResult>> ProcessAsync(
        IReadOnlyList<string> inputPaths,
        BatchOptions options,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>释放底层引擎缓存的推理会话。</summary>
    void ClearSessionCache();
}
