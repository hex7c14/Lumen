using Lumen.Core;
using Lumen.Core.Batch;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Settings;
using Lumen.Core.Upscaling;

namespace Lumen.App.Services;

/// <summary>
/// 手工装配的进程级服务集合（没有 DI 容器，保持可读）。
/// UI 只通过 Core 的公共契约编程：<see cref="IImageCodec"/>、<see cref="IModelCatalog"/>、
/// <see cref="IComputeEnvironment"/>、<see cref="IUpscalerService"/>、<see cref="IBatchUpscalerService"/>。
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>调试用：LUMEN_FORCE_NO_DML=1 时让 UI 认为 DirectML 不可用（验证自动降级路径）。</summary>
    private static readonly bool ForceNoDirectMl =
        string.Equals(Environment.GetEnvironmentVariable("LUMEN_FORCE_NO_DML"), "1", StringComparison.Ordinal);

    private AppServices(
        IImageCodec codec,
        IModelCatalog catalog,
        ISettingsStore settingsStore,
        AppSettings settings,
        IComputeEnvironment compute,
        IUpscalerService? upscaler,
        IBatchUpscalerService? batchUpscaler)
    {
        Codec = codec;
        Catalog = catalog;
        SettingsStore = settingsStore;
        Settings = settings;
        Compute = compute;
        Upscaler = upscaler;
        BatchUpscaler = batchUpscaler;
        FileDialogs = new FileDialogService();
    }

    /// <summary>图像编解码与重采样（Skia）。</summary>
    public IImageCodec Codec { get; }

    /// <summary>快速缩略图加载（若 codec 实现 <see cref="IImageThumbnailLoader"/>；JPEG 走缩放解码）。</summary>
    public IImageThumbnailLoader? ThumbnailLoader => Codec as IImageThumbnailLoader;

    /// <summary>models/*/model.json 扫描结果。</summary>
    public IModelCatalog Catalog { get; }

    public ISettingsStore SettingsStore { get; }

    /// <summary>启动时加载的设置；退出前由 ViewModel 写回。</summary>
    public AppSettings Settings { get; }

    public IComputeEnvironment Compute { get; }

    /// <summary>UI 视角下 DirectML 是否可用（含 LUMEN_FORCE_NO_DML 调试覆盖）。</summary>
    public bool DirectMlAvailable => !ForceNoDirectMl && Compute.DirectMlAvailable;

    /// <summary>
    /// 超分引擎（engine-dev 的 <see cref="OnnxUpscalerService"/>）。初始化失败时为 null，界面给出提示且不崩溃。
    /// </summary>
    public IUpscalerService? Upscaler { get; }

    /// <summary>
    /// 批量超分服务（engine-dev 的 <see cref="BatchUpscalerService"/>，复用同一个引擎实例以共享 ONNX 会话缓存）。
    /// 装配失败时为 null，界面会给出“批处理不可用”的中文提示。
    /// </summary>
    public IBatchUpscalerService? BatchUpscaler { get; }

    /// <summary>文件/文件夹选择器（需要窗口句柄，窗口创建后设置 Owner）。</summary>
    public FileDialogService FileDialogs { get; }

    public static AppServices CreateDefault()
    {
        var codec = new SkiaImageCodec();
        IModelCatalog catalog = new ModelCatalog(AppPaths.ModelsRoot);
        ISettingsStore settingsStore = new JsonSettingsStore();
        AppSettings settings = settingsStore.Load();
        IComputeEnvironment compute = ComputeEnvironment.Detect();

        IUpscalerService? upscaler = null;
        try
        {
            upscaler = new OnnxUpscalerService(codec);
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化超分引擎失败，界面将以“引擎不可用”状态运行。", ex);
        }

        IBatchUpscalerService? batchUpscaler = null;
        if (upscaler is not null)
        {
            try
            {
                // 复用单图流程的引擎实例：单图预览与批量处理共享同一个 ONNX 会话缓存。
                batchUpscaler = new BatchUpscalerService(upscaler, codec);
            }
            catch (Exception ex)
            {
                AppLog.Error("初始化批处理服务失败。", ex);
            }
        }

        AppLog.Info($"模型目录：{catalog.ModelsRoot}，可用模型 {catalog.Models.Count} 个。");
        AppLog.Info($"计算环境：{compute.Summary}{(ForceNoDirectMl ? "（调试：强制视为无 DirectML）" : string.Empty)}");
        AppLog.Info(batchUpscaler is null ? "批处理服务不可用。" : $"批处理服务已就绪：{batchUpscaler.GetType().Name}");

        return new AppServices(codec, catalog, settingsStore, settings, compute, upscaler, batchUpscaler);
    }

    public void Dispose()
    {
        Upscaler?.Dispose();
    }
}
