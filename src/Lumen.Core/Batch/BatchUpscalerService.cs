using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Batch;

/// <summary>
/// 批量超分服务：顺序处理多张图片，逐张 解码 → 超分 → 保存；单张失败不中断整批。
/// 内部复用同一个 <see cref="IUpscalerService"/>，因此 ONNX 会话在图片之间复用（首张后不再重建）。
/// 取消语义：取消后当前项与后续项标记为 <see cref="BatchItemStatus.Canceled"/>，不抛异常。
/// </summary>
public sealed class BatchUpscalerService : IBatchUpscalerService, IDisposable
{
    private readonly IUpscalerService _upscaler;
    private readonly IImageCodec _codec;
    private readonly bool _ownsUpscaler;
    private int _disposed;

    /// <summary>创建默认组合（内部自建 ONNX 引擎，Dispose 时一并释放）。</summary>
    public BatchUpscalerService()
        : this(new OnnxUpscalerService(), new SkiaImageCodec(), ownsUpscaler: true)
    {
    }

    /// <summary>注入既有引擎/编解码器（UI 可共享单图流程的引擎实例，进一步复用会话）。</summary>
    public BatchUpscalerService(IUpscalerService upscaler, IImageCodec codec)
        : this(upscaler, codec, ownsUpscaler: false)
    {
    }

    private BatchUpscalerService(IUpscalerService upscaler, IImageCodec codec, bool ownsUpscaler)
    {
        _upscaler = upscaler ?? throw new ArgumentNullException(nameof(upscaler));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _ownsUpscaler = ownsUpscaler;
    }

    public Task<IReadOnlyList<BatchItemResult>> ProcessAsync(
        IReadOnlyList<string> inputPaths,
        BatchOptions options,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        options.Validate();

        if (inputPaths.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<BatchItemResult>>([]);
        }

        foreach (string path in inputPaths)
        {
            if (path is null)
            {
                throw new ArgumentException("输入文件列表包含 null。", nameof(inputPaths));
            }
        }

        // 取消不抛异常（由结果表达），因此 Task.Run 不绑定 cancellationToken。
        return Task.Run(() => ProcessCoreAsync(inputPaths, options, progress, cancellationToken), CancellationToken.None);
    }

    public void ClearSessionCache() => _upscaler.ClearSessionCache();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsUpscaler && _upscaler is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task<IReadOnlyList<BatchItemResult>> ProcessCoreAsync(
        IReadOnlyList<string> inputPaths,
        BatchOptions options,
        IProgress<BatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        int count = inputPaths.Count;
        var results = new BatchItemResult[count];
        var batchWatch = Stopwatch.StartNew();
        int previewMaxDimension = ResolvePreviewMaxDimension(options);
        UpscaleOptions baseOptions = options.Upscale;
        UpscaleOptions itemOptions = WithPreviewMaxDimension(baseOptions, previewMaxDimension);

        // 流水线：最多 1 张结果在后台保存，同时开始解码/推理下一张。
        // 保存失败只把对应项标为 Failed，不取消整批；保存完成（成功/失败）前必须在返回前收尾。
        PendingSave? pendingSave = null;
        // 保存是异步的：上一张文件还没落盘时，下一张的命名冲突检查必须靠这个“批内已预留路径”集合，
        // 否则不同源目录的同名文件会撞到同一个输出路径（AutoRename 失效）。
        var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task FinalizePendingSaveAsync()
        {
            if (pendingSave is null)
            {
                return;
            }

            PendingSave pending = pendingSave;
            pendingSave = null;
            try
            {
                TimeSpan saveElapsed = await pending.SaveTask.ConfigureAwait(false);
                results[pending.Index] = new BatchItemResult
                {
                    InputPath = pending.InputPath,
                    OutputPath = pending.OutputPath,
                    Status = BatchItemStatus.Completed,
                    InputWidth = pending.InputWidth,
                    InputHeight = pending.InputHeight,
                    OutputWidth = pending.OutputWidth,
                    OutputHeight = pending.OutputHeight,
                    Duration = pending.InferenceElapsed + saveElapsed,
                    Preview = pending.Preview,
                    Backend = pending.Backend,
                    Precision = pending.Precision,
                };
                Report(
                    progress,
                    pending.Index,
                    count,
                    pending.InputPath,
                    BatchItemStage.Completed,
                    1,
                    pending.TilesCompleted,
                    pending.TilesTotal,
                    pending.OutputPath,
                    "完成",
                    batchWatch.Elapsed);
            }
            catch (Exception ex)
            {
                string message = DescribeFailure(ex);
                results[pending.Index] = Failed(pending.InputPath, message);
                AppLog.Error($"批处理保存失败：{pending.InputPath}：{message}", ex);
                Report(
                    progress,
                    pending.Index,
                    count,
                    pending.InputPath,
                    BatchItemStage.Failed,
                    1,
                    0,
                    0,
                    pending.OutputPath,
                    message,
                    batchWatch.Elapsed);
            }
        }

        for (int index = 0; index < count; index++)
        {
            string inputPath = inputPaths[index];
            Report(progress, index, count, inputPath, BatchItemStage.Pending, 0, 0, 0, null, "等待处理", batchWatch.Elapsed);

            if (cancellationToken.IsCancellationRequested)
            {
                await FinalizePendingSaveAsync().ConfigureAwait(false);
                results[index] = Canceled(inputPath);
                Report(progress, index, count, inputPath, BatchItemStage.Canceled, 1, 0, 0, null, "已取消", batchWatch.Elapsed);
                continue;
            }

            var itemWatch = Stopwatch.StartNew();
            string? outputPath = null;
            try
            {
                Report(progress, index, count, inputPath, BatchItemStage.Loading, 0.02, 0, 0, null, "正在解码", batchWatch.Elapsed);
                cancellationToken.ThrowIfCancellationRequested();

                LoadedImage image = _codec.Load(inputPath);
                int inputWidth = image.Width;
                int inputHeight = image.Height;

                Report(progress, index, count, inputPath, BatchItemStage.Loading, 0.05, 0, 0, null, "正在解码", batchWatch.Elapsed);

                // 命名与冲突处理放在解码之后（Skip 策略需要知道输出路径；解码成功也说明输入有效）。
                string pattern = options.FileNamePattern;
                string fileName = BatchNaming.Expand(
                    pattern,
                    inputPath,
                    index + 1,
                    itemOptions.TargetScale,
                    itemOptions.Model.Id,
                    DateTime.UtcNow);
                outputPath = BatchNaming.BuildOutputPath(options.OutputDirectory, inputPath, fileName, options.Save.Format);
                string? resolved = ResolveBatchConflict(outputPath, options.ConflictPolicy, reservedOutputPaths);
                if (resolved is null)
                {
                    reservedOutputPaths.Add(outputPath);
                    await FinalizePendingSaveAsync().ConfigureAwait(false);
                    results[index] = Skipped(inputPath, outputPath);
                    Report(progress, index, count, inputPath, BatchItemStage.Skipped, 1, 0, 0, outputPath, "已跳过（输出文件已存在）", batchWatch.Elapsed);
                    continue;
                }

                outputPath = resolved;
                reservedOutputPaths.Add(outputPath);

                cancellationToken.ThrowIfCancellationRequested();
                // 用同步 IProgress 而不是 Progress<T>：保证进度回调按顺序发生在当前项生命周期内，
                // UI 层自己的 IProgress 实现负责跨线程封送。
                var upscaleProgress = new InlineProgress<UpscaleProgress>(value =>
                {
                    double itemFraction = 0.05 + (0.90 * Math.Clamp(value.Fraction, 0.0, 1.0));
                    Report(
                        progress,
                        index,
                        count,
                        inputPath,
                        BatchItemStage.Upscaling,
                        itemFraction,
                        value.TilesCompleted,
                        value.TilesTotal,
                        outputPath,
                        value.Message,
                        batchWatch.Elapsed);
                });

                UpscaleResult result = await _upscaler.UpscaleAsync(image, itemOptions, upscaleProgress, cancellationToken)
                    .ConfigureAwait(false);

                // 当前项推理期间，上一张的保存已在后台完成；这里收尾并上报上一项。
                cancellationToken.ThrowIfCancellationRequested();
                await FinalizePendingSaveAsync().ConfigureAwait(false);

                itemWatch.Stop();
                Report(progress, index, count, inputPath, BatchItemStage.Saving, 0.97, result.TileCount, result.TileCount, outputPath, "正在保存", batchWatch.Elapsed);

                ImagePixels outputPixels = result.Output;
                SaveOptions saveOptions = options.Save;
                string savePath = outputPath;
                Task<TimeSpan> saveTask = Task.Run(() =>
                {
                    var saveWatch = Stopwatch.StartNew();
                    _codec.Save(outputPixels, savePath, saveOptions);
                    saveWatch.Stop();
                    return saveWatch.Elapsed;
                });

                pendingSave = new PendingSave
                {
                    Index = index,
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    InputWidth = inputWidth,
                    InputHeight = inputHeight,
                    OutputWidth = result.OutputWidth,
                    OutputHeight = result.OutputHeight,
                    InferenceElapsed = itemWatch.Elapsed,
                    Preview = options.KeepPreview ? result.Preview : null,
                    Backend = result.Backend,
                    Precision = result.Precision,
                    TilesCompleted = result.TileCount,
                    TilesTotal = result.TileCount,
                    SaveTask = saveTask,
                };
            }
            catch (OperationCanceledException)
            {
                itemWatch.Stop();
                await FinalizePendingSaveAsync().ConfigureAwait(false);
                results[index] = Canceled(inputPath);
                AppLog.Info($"批处理已取消：{inputPath}");
                Report(progress, index, count, inputPath, BatchItemStage.Canceled, 1, 0, 0, outputPath, "已取消", batchWatch.Elapsed);
            }
            catch (Exception ex)
            {
                itemWatch.Stop();
                await FinalizePendingSaveAsync().ConfigureAwait(false);
                string message = DescribeFailure(ex);
                results[index] = Failed(inputPath, message);
                AppLog.Error($"批处理单项失败：{inputPath}：{message}", ex);
                Report(progress, index, count, inputPath, BatchItemStage.Failed, 1, 0, 0, outputPath, message, batchWatch.Elapsed);
            }
        }

        await FinalizePendingSaveAsync().ConfigureAwait(false);

        batchWatch.Stop();
        AppLog.Info(
            $"批处理结束：共 {count} 张，成功 {results.Count(r => r.Status == BatchItemStatus.Completed)}，" +
            $"失败 {results.Count(r => r.Status == BatchItemStatus.Failed)}，" +
            $"跳过 {results.Count(r => r.Status == BatchItemStatus.Skipped)}，取消 {results.Count(r => r.Status == BatchItemStatus.Canceled)}，" +
            $"耗时 {batchWatch.ElapsedMilliseconds}ms");
        return results;
    }

    /// <summary>一张已完成推理、正在后台保存的结果。</summary>
    private sealed class PendingSave
    {
        public required int Index { get; init; }

        public required string InputPath { get; init; }

        public required string OutputPath { get; init; }

        public required int InputWidth { get; init; }

        public required int InputHeight { get; init; }

        public required int OutputWidth { get; init; }

        public required int OutputHeight { get; init; }

        public required TimeSpan InferenceElapsed { get; init; }

        public ImagePixels? Preview { get; init; }

        public required string Backend { get; init; }

        public required ModelPrecision Precision { get; init; }

        public required int TilesCompleted { get; init; }

        public required int TilesTotal { get; init; }

        public required Task<TimeSpan> SaveTask { get; init; }
    }

    /// <summary>冲突解析：在磁盘存在性之外，额外考虑本批已预留（正在后台保存/已跳过）的路径。</summary>
    private static string? ResolveBatchConflict(string desiredPath, OutputConflictPolicy policy, HashSet<string> reserved)
    {
        switch (policy)
        {
            case OutputConflictPolicy.Overwrite:
                return desiredPath;
            case OutputConflictPolicy.Skip:
                return File.Exists(desiredPath) || reserved.Contains(desiredPath) ? null : desiredPath;
            default:
                break;
        }

        if (!File.Exists(desiredPath) && !reserved.Contains(desiredPath))
        {
            return desiredPath;
        }

        string directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(candidate) && !reserved.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为“{Path.GetFileName(desiredPath)}”找到不冲突的输出文件名。");
    }

    private static string DescribeFailure(Exception exception) => exception switch    {
        InvalidDataException => $"图片无法解码（可能已损坏或格式不受支持）：{exception.Message}",
        FileNotFoundException => $"文件不存在：{exception.Message}",
        DirectoryNotFoundException => $"目录不存在：{exception.Message}",
        UnauthorizedAccessException => $"没有读写权限：{exception.Message}",
        IOException => $"读写文件失败：{exception.Message}",
        OutOfMemoryException => "内存不足：图片过大或批量预览占用过多内存。",
        NotSupportedException => exception.Message,
        ArgumentException => exception.Message,
        InvalidOperationException => exception.Message,
        _ => $"处理失败：{exception.Message}",
    };

    /// <summary>UpscaleOptions.PreviewMaxDimension 与 BatchOptions.PreviewMaxDimension 取较小值。</summary>
    private static int ResolvePreviewMaxDimension(BatchOptions options)
    {
        if (!options.KeepPreview)
        {
            return 0;
        }

        int item = options.Upscale.PreviewMaxDimension;
        int batch = options.PreviewMaxDimension;
        if (item <= 0)
        {
            return Math.Max(0, batch);
        }

        if (batch <= 0)
        {
            return 0;
        }

        return Math.Min(item, batch);
    }

    /// <summary>UpscaleOptions 的属性都是 init-only，这里复制一份并替换预览长边。</summary>
    private static UpscaleOptions WithPreviewMaxDimension(UpscaleOptions source, int previewMaxDimension)
    {
        if (source.PreviewMaxDimension == previewMaxDimension)
        {
            return source;
        }

        return new UpscaleOptions
        {
            Model = source.Model,
            TargetScale = source.TargetScale,
            Backend = source.Backend,
            PreferFp16 = source.PreferFp16,
            TileSize = source.TileSize,
            TilePadding = source.TilePadding,
            MaxOutputPixels = source.MaxOutputPixels,
            PreserveAlpha = source.PreserveAlpha,
            PreviewMaxDimension = previewMaxDimension,
            GpuDeviceId = source.GpuDeviceId,
            CpuThreads = source.CpuThreads,
            CpuCompatibilityMode = source.CpuCompatibilityMode,
        };
    }

    private static BatchItemResult Canceled(string inputPath) => new()
    {
        InputPath = inputPath,
        Status = BatchItemStatus.Canceled,
        ErrorMessage = "已取消",
    };

    /// <summary>同步执行回调的 IProgress 实现（批处理内部使用，保证进度顺序确定）。</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    private static BatchItemResult Failed(string inputPath, string message) => new()
    {
        InputPath = inputPath,
        Status = BatchItemStatus.Failed,
        ErrorMessage = message,
    };

    private static BatchItemResult Skipped(string inputPath, string? outputPath) => new()
    {
        InputPath = inputPath,
        Status = BatchItemStatus.Skipped,
        OutputPath = outputPath,
        ErrorMessage = "输出文件已存在，按冲突策略跳过。",
    };

    private static void Report(
        IProgress<BatchProgress>? progress,
        int itemIndex,
        int itemCount,
        string inputPath,
        BatchItemStage stage,
        double itemFraction,
        int tilesCompleted,
        int tilesTotal,
        string? outputPath,
        string? message,
        TimeSpan elapsed)
    {
        if (progress is null)
        {
            return;
        }

        double clampedItem = Math.Clamp(itemFraction, 0.0, 1.0);
        progress.Report(new BatchProgress
        {
            ItemIndex = itemIndex,
            ItemCount = itemCount,
            InputPath = inputPath,
            Stage = stage,
            OverallFraction = Math.Clamp((itemIndex + clampedItem) / itemCount, 0.0, 1.0),
            ItemFraction = clampedItem,
            TilesCompleted = tilesCompleted,
            TilesTotal = tilesTotal,
            OutputPath = outputPath,
            Message = message,
            Elapsed = elapsed,
        });
    }
}
