using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Inference;
using Lumen.Core.Models;

namespace Lumen.Core.Upscaling;

/// <summary>单块推理的耗时记录，供 CLI bench 输出每块明细（GUI 不需要）。</summary>
public readonly record struct TileTiming(int Index, int X, int Y, int Width, int Height, TimeSpan Elapsed);

/// <summary>
/// ONNX Runtime 超分引擎：DirectML/CPU 会话缓存、分块推理、拼接、透明通道、目标倍率降采样与预览生成。
/// 实现细节见 docs/ARCHITECTURE.md §5。所有推理在 <see cref="Task.Run(Action)"/> 的工作线程上串行执行，
/// UI 直接 await；同一时刻只有一个会话在跑推理。
/// </summary>
public sealed class OnnxUpscalerService : IUpscalerService
{
    private readonly object _cacheGate = new();
    private readonly Dictionary<SessionCacheKey, OnnxSessionHandle> _sessionCache = [];
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly IImageCodec _codec;

    private TileTiming[] _lastTileTimings = [];
    private long _lastSessionCreationTicks;
    private int _lastTilePadding;
    private int _disposed;

    public OnnxUpscalerService()
        : this(new SkiaImageCodec())
    {
    }

    public OnnxUpscalerService(IImageCodec codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>最近一次 <see cref="UpscaleAsync"/> 的每块耗时（含张量构建/推理/写回，不含会话创建）。</summary>
    public IReadOnlyList<TileTiming> LastTileTimings => Volatile.Read(ref _lastTileTimings);

    /// <summary>最近一次 <see cref="UpscaleAsync"/> 为创建（或命中）会话等待的时间；缓存命中时为 0。</summary>
    public TimeSpan LastSessionCreationTime => TimeSpan.FromTicks(Interlocked.Read(ref _lastSessionCreationTicks));

    /// <summary>最近一次运行实际使用的上下文边距（自动解析后的值），CLI bench 用来打印。</summary>
    public int LastTilePadding => Volatile.Read(ref _lastTilePadding);

    /// <summary>
    /// 实验特性：把每个块的输入张量反射填充到统一尺寸 (tile+2*pad)²，使 DirectML 在整次运行中
    /// 只看到一种输入形状（消除边块形状差异）。实测提速 14–33%，默认开启；CLI 可用
    /// <c>--uniform-pad off</c> 关闭。见 docs/PERFORMANCE.md。
    /// </summary>
    public bool PadTilesToUniformShape { get; set; } = true;

    public Task<UpscaleResult> UpscaleAsync(
        LoadedImage input,
        UpscaleOptions options,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        options.Validate();

        // 内部同步实现放到线程池；调用方（UI）直接 await。
        return Task.Run(() => UpscaleCore(input, options, progress, cancellationToken), cancellationToken);
    }

    public void ClearSessionCache()
    {
        lock (_cacheGate)
        {
            foreach (OnnxSessionHandle handle in _sessionCache.Values)
            {
                handle.Dispose();
            }

            _sessionCache.Clear();
        }

        AppLog.Info("已释放缓存的 ONNX 推理会话。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClearSessionCache();
        _runGate.Dispose();
        AppLog.Info("OnnxUpscalerService 已释放。");
    }

    private UpscaleResult UpscaleCore(
        LoadedImage input,
        UpscaleOptions options,
        IProgress<UpscaleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        ModelDescriptor model = options.Model;
        ImagePixels source = input.Pixels;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0.0, UpscaleStage.Preparing, 0, 0, total.Elapsed, "正在准备");

            int width = source.Width;
            int height = source.Height;
            long modelScale = model.Scale;
            long outputWidthLong = width * modelScale;
            long outputHeightLong = height * modelScale;
            long outputPixels = outputWidthLong * outputHeightLong;
            if (outputPixels > options.MaxOutputPixels)
            {
                throw new InvalidOperationException(
                    $"输出尺寸 {outputWidthLong}x{outputHeightLong}（{outputPixels:N0} 像素）超过安全上限 " +
                    $"{options.MaxOutputPixels:N0} 像素，已阻止处理以避免内存耗尽。请换用更小的图片或降低目标倍率。");
            }

            if (outputPixels * ImagePixels.BytesPerPixel > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"输出像素缓冲需要 {outputPixels * ImagePixels.BytesPerPixel:N0} 字节，超出单数组上限；请降低倍率或尺寸。");
            }

            int outputWidth = (int)outputWidthLong;
            int outputHeight = (int)outputHeightLong;

            bool preserveAlpha = options.PreserveAlpha && input.HasTransparency;
            bool flattenWhite = !options.PreserveAlpha && input.HasTransparency;
            ImagePixels inferenceInput = flattenWhite ? FlattenOntoWhite(source) : source;
            if (flattenWhite)
            {
                AppLog.Info("输入含透明像素且未要求保留 alpha：先合成到白底再推理。");
            }

            // 会话获取 + 分块推理整体串行化：同一时刻只有一次 Upscale 在跑，
            // 也避免会话创建/淘汰（见 GetOrCreateSession 的跨精度淘汰）与正在进行的推理并发冲突。
            OnnxSessionHandle handle;
            TilePlan plan;
            ImagePixels output;
            TimeSpan sessionCreation;
            _runGate.Wait(cancellationToken);
            try
            {
                handle = GetOrCreateSession(model, options, cancellationToken, out sessionCreation);
                Interlocked.Exchange(ref _lastSessionCreationTicks, sessionCreation.Ticks);
                Report(progress, 0.05, UpscaleStage.Preparing, 0, 0, total.Elapsed, "模型已就绪");

                int tileSize;
                if (options.TileSize > 0)
                {
                    tileSize = options.TileSize;
                }
                else if (handle.EffectiveBackend == ExecutionBackend.Cpu && options.CpuCompatibilityMode)
                {
                    // CPU 兼容模式：稳定优先的保守分块（见 docs/PERFORMANCE.md CPU 章节）。
                    tileSize = TilePlanner.ResolveCpuCompatibilityTileSize(model.Arch);
                }
                else
                {
                    tileSize = TilePlanner.ResolveAutoTileSize(handle.EffectiveBackend, model.Arch, width, height);
                }

                int padding = options.TilePadding > 0
                    ? options.TilePadding
                    : TilePlanner.ResolveAutoPadding(tileSize, model.Arch);
                plan = TilePlanner.Create(width, height, tileSize, padding);
                Volatile.Write(ref _lastTilePadding, plan.PaddingX);

                AppLog.Info(
                    $"开始超分：{width}x{height} → {outputWidth}x{outputHeight}，模型={model.Id} 后端={handle.BackendDisplayName} " +
                    $"精度={handle.Precision} tile={plan.TileWidth} pad={plan.PaddingX} 块数={plan.Count} " +
                    $"uniformTile={PadTilesToUniformShape} cpuCompat={options.CpuCompatibilityMode} " +
                    $"threads={(options.CpuThreads > 0 ? options.CpuThreads : 0)}");

                output = new ImagePixels(outputWidth, outputHeight);
                if (!preserveAlpha)
                {
                    FillAlpha(output, 255);
                }

                RunTiles(handle, inferenceInput, output, plan, model.Scale, preserveAlpha, progress, total, cancellationToken);
            }
            finally
            {
                _runGate.Release();
            }

            Report(progress, 0.97, UpscaleStage.PostProcessing, plan.Count, plan.Count, total.Elapsed, "正在合成输出");

            ImagePixels finalPixels = output;
            if (preserveAlpha)
            {
                finalPixels = ComposeAlpha(source, output, model.Scale, options.TargetScale);
            }
            else if (options.TargetScale < model.Scale)
            {
                // 目标倍率小于模型倍率：Mitchell 三次降采样。
                // （Lanczos3 试验：2x +0.21dB、3x -0.15dB，无稳定收益且慢约 6 倍，
                //   见 docs/PERFORMANCE.md §8，已回退。）
                finalPixels = _codec.Resize(
                    output,
                    width * options.TargetScale,
                    height * options.TargetScale,
                    ResampleMode.HighQuality);
            }

            ImagePixels? preview = options.PreviewMaxDimension > 0
                ? _codec.CreatePreview(finalPixels, options.PreviewMaxDimension, options.PreviewMaxDimension)
                : null;

            total.Stop();
            Report(progress, 1.0, UpscaleStage.Completed, plan.Count, plan.Count, total.Elapsed, "完成");

            var result = new UpscaleResult
            {
                Output = finalPixels,
                Preview = preview,
                Duration = total.Elapsed,
                Backend = handle.BackendDisplayName,
                ModelId = model.Id,
                Precision = handle.Precision,
                TileSize = plan.TileWidth,
                TileCount = plan.Count,
            };

            AppLog.Info(
                $"超分完成：{finalPixels.Width}x{finalPixels.Height}，后端={result.Backend} 精度={result.Precision} " +
                $"块数={result.TileCount} 总耗时={total.ElapsedMilliseconds}ms 会话创建={sessionCreation.TotalMilliseconds:F0}ms");
            return result;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"超分已取消（模型 {model.Id}，已用时 {total.ElapsedMilliseconds}ms）。");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"超分失败（模型 {model.Id}）：{ex.Message}", ex);
            throw;
        }
    }

    private void RunTiles(
        OnnxSessionHandle handle,
        ImagePixels inferenceInput,
        ImagePixels output,
        TilePlan plan,
        int scale,
        bool preserveAlpha,
        IProgress<UpscaleProgress>? progress,
        Stopwatch total,
        CancellationToken cancellationToken)
    {
        var timings = new TileTiming[plan.Count];
        for (int i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TileRect tile = plan.Tiles[i];
            var tileWatch = Stopwatch.StartNew();
            TileInference.Run(
                handle,
                inferenceInput,
                output,
                tile,
                plan.PaddingX,
                plan.PaddingY,
                scale,
                preserveAlpha,
                PadTilesToUniformShape);
            tileWatch.Stop();
            timings[i] = new TileTiming(i, tile.X, tile.Y, tile.Width, tile.Height, tileWatch.Elapsed);

            double fraction = 0.05 + (0.9 * (i + 1) / plan.Count);
            Report(
                progress,
                fraction,
                UpscaleStage.Inference,
                i + 1,
                plan.Count,
                total.Elapsed,
                $"正在推理 {i + 1}/{plan.Count}");
        }

        Volatile.Write(ref _lastTileTimings, timings);
    }

    /// <summary>
    /// 透明图合成：模型输出是“预乘 alpha”的 RGB（输入同样预乘，避免透明区域垃圾值干扰模型），
    /// alpha 通道用 codec 高质量重采样到输出尺寸后合并；若目标倍率小于模型倍率，则在合成后整体降采样，
    /// 最后反预乘回直通 alpha 的 RGBA。
    /// </summary>
    private ImagePixels ComposeAlpha(ImagePixels source, ImagePixels premultipliedOutput, int modelScale, int targetScale)
    {
        int modelWidth = premultipliedOutput.Width;
        int modelHeight = premultipliedOutput.Height;

        // alpha 单独重采样：做成灰度 RGBA，让 codec 用 Mitchell 三次采样处理。
        var alphaSource = new ImagePixels(source.Width, source.Height);
        byte[] sourcePixels = source.Pixels;
        byte[] alphaPixels = alphaSource.Pixels;
        for (int i = 0; i < sourcePixels.Length; i += ImagePixels.BytesPerPixel)
        {
            byte alpha = sourcePixels[i + 3];
            alphaPixels[i] = alpha;
            alphaPixels[i + 1] = alpha;
            alphaPixels[i + 2] = alpha;
            alphaPixels[i + 3] = alpha;
        }

        ImagePixels alphaModel = _codec.Resize(alphaSource, modelWidth, modelHeight, ResampleMode.HighQuality);
        byte[] alphaBuffer = alphaModel.Pixels;
        byte[] premultipliedBuffer = premultipliedOutput.Pixels;
        for (int i = 0; i < premultipliedBuffer.Length; i += ImagePixels.BytesPerPixel)
        {
            premultipliedBuffer[i + 3] = alphaBuffer[i];
        }

        ImagePixels composed;
        if (targetScale < modelScale)
        {
            composed = _codec.Resize(
                premultipliedOutput,
                modelWidth / modelScale * targetScale,
                modelHeight / modelScale * targetScale,
                ResampleMode.HighQuality);
        }
        else
        {
            composed = premultipliedOutput;
        }

        Unpremultiply(composed);
        return composed;
    }

    private static void Unpremultiply(ImagePixels image)
    {
        byte[] pixels = image.Pixels;
        for (int i = 0; i < pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            int alpha = pixels[i + 3];
            if (alpha == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }

            pixels[i] = UnpremultiplyComponent(pixels[i], alpha);
            pixels[i + 1] = UnpremultiplyComponent(pixels[i + 1], alpha);
            pixels[i + 2] = UnpremultiplyComponent(pixels[i + 2], alpha);
        }

        static byte UnpremultiplyComponent(int value, int alpha)
        {
            int straight = ((value * 255) + (alpha / 2)) / alpha;
            return (byte)Math.Min(255, straight);
        }
    }

    private OnnxSessionHandle GetOrCreateSession(
        ModelDescriptor model,
        UpscaleOptions options,
        CancellationToken cancellationToken,
        out TimeSpan elapsed)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelPrecision precision = ResolvePrecision(model, options);
        string modelPath = model.ResolveModelPath(precision);
        int effectiveCpuThreads = ResolveEffectiveCpuThreads(options);
        var key = new SessionCacheKey(modelPath, precision, options.Backend, options.GpuDeviceId, effectiveCpuThreads);

        lock (_cacheGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_sessionCache.TryGetValue(key, out OnnxSessionHandle? cached))
            {
                elapsed = TimeSpan.Zero;
                return cached;
            }

            // 同一模型（权重文件）同时保留 fp32 + fp16 两个 DirectML 会话会争抢 8GB 显存，
            // 实测偶发极端变慢；新建某一精度的会话前，先释放同一模型其他精度的缓存会话。
            EvictOtherPrecisionsLocked(modelPath, precision);

            var watch = Stopwatch.StartNew();
            OnnxSessionHandle handle = OnnxSessionFactory.Create(
                model,
                precision,
                options.Backend,
                options.GpuDeviceId,
                effectiveCpuThreads,
                out string? fallbackNote);
            watch.Stop();
            if (fallbackNote is not null)
            {
                AppLog.Warn($"后端回退到 {handle.BackendDisplayName}：{fallbackNote}");
            }

            _sessionCache[key] = handle;
            elapsed = watch.Elapsed;
            return handle;
        }
    }

    /// <summary>
    /// 释放同一模型路径下不同精度的缓存会话（调用方必须持有 <see cref="_cacheGate"/>，
    /// 且必须保证当前没有推理在用这些会话——UpscaleCore 用 <see cref="_runGate"/> 串行化保证）。
    /// </summary>
    private void EvictOtherPrecisionsLocked(string modelPath, ModelPrecision keepPrecision)
    {
        List<SessionCacheKey>? stale = null;
        foreach (SessionCacheKey key in _sessionCache.Keys)
        {
            if (key.Precision != keepPrecision &&
                string.Equals(key.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                (stale ??= []).Add(key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (SessionCacheKey key in stale)
        {
            if (_sessionCache.Remove(key, out OnnxSessionHandle? handle))
            {
                handle.Dispose();
                AppLog.Info($"已释放同模型其他精度的缓存会话：{Path.GetFileName(key.ModelPath)} ({key.Precision}, {key.Backend})");
            }
        }
    }

    /// <summary>
    /// CPU 后端实际使用的 IntraOp 线程数：显式 CpuThreads 优先；CPU 兼容模式未指定时
    /// 用保守上限（min(8, 核数/2)）——实测 32 逻辑核机器上 8 线程比 ORT 自动快约 13%，
    /// 也避免低端机超订阅。0 仍表示交给 ORT 决定（非兼容模式）。
    /// </summary>
    private static int ResolveEffectiveCpuThreads(UpscaleOptions options)
    {
        if (options.Backend != ExecutionBackend.Cpu)
        {
            return 0;
        }

        if (options.CpuThreads > 0)
        {
            return options.CpuThreads;
        }

        if (options.CpuCompatibilityMode)
        {
            return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        }

        return 0;
    }

    /// <summary>
    /// 解析精度：要求 fp16、模型声明了 fp16 且文件确实存在时才用 fp16；CPU 后端强制 fp32。
    /// （model.json 可能声明了 fp16 但文件缺失，这里兜底并记录日志。）
    /// </summary>
    private static ModelPrecision ResolvePrecision(ModelDescriptor model, UpscaleOptions options)
    {
        ModelPrecision precision = options.PreferFp16 && model.HasFp16 ? ModelPrecision.Fp16 : ModelPrecision.Fp32;
        if (precision == ModelPrecision.Fp16 && !File.Exists(model.ResolveModelPath(ModelPrecision.Fp16)))
        {
            AppLog.Warn($"模型 {model.Id} 声明了 fp16 权重但文件缺失，回退 fp32：{model.ResolveModelPath(ModelPrecision.Fp16)}");
            precision = ModelPrecision.Fp32;
        }

        if (options.Backend == ExecutionBackend.Cpu)
        {
            precision = ModelPrecision.Fp32;
        }

        return precision;
    }

    private static void FillAlpha(ImagePixels image, byte value)
    {
        byte[] pixels = image.Pixels;
        for (int i = ImagePixels.BytesPerPixel - 1; i < pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            pixels[i] = value;
        }
    }

    /// <summary>把透明像素合成到白底，得到不透明的推理输入（仅在 PreserveAlpha=false 时使用）。</summary>
    private static ImagePixels FlattenOntoWhite(ImagePixels source)
    {
        var result = new ImagePixels(source.Width, source.Height);
        byte[] sourcePixels = source.Pixels;
        byte[] destination = result.Pixels;
        for (int i = 0; i < sourcePixels.Length; i += ImagePixels.BytesPerPixel)
        {
            int alpha = sourcePixels[i + 3];
            if (alpha == 255)
            {
                destination[i] = sourcePixels[i];
                destination[i + 1] = sourcePixels[i + 1];
                destination[i + 2] = sourcePixels[i + 2];
            }
            else
            {
                int inverse = 255 - alpha;
                destination[i] = (byte)(((sourcePixels[i] * alpha) + (255 * inverse) + 127) / 255);
                destination[i + 1] = (byte)(((sourcePixels[i + 1] * alpha) + (255 * inverse) + 127) / 255);
                destination[i + 2] = (byte)(((sourcePixels[i + 2] * alpha) + (255 * inverse) + 127) / 255);
            }

            destination[i + 3] = 255;
        }

        return result;
    }

    private static void Report(
        IProgress<UpscaleProgress>? progress,
        double fraction,
        UpscaleStage stage,
        int completed,
        int total,
        TimeSpan elapsed,
        string? message)
    {
        progress?.Report(new UpscaleProgress
        {
            Fraction = Math.Clamp(fraction, 0.0, 1.0),
            Stage = stage,
            TilesCompleted = completed,
            TilesTotal = total,
            Elapsed = elapsed,
            Message = message,
        });
    }

    private readonly record struct SessionCacheKey(
        string ModelPath,
        ModelPrecision Precision,
        ExecutionBackend Backend,
        int DeviceId,
        int CpuThreads);
}
