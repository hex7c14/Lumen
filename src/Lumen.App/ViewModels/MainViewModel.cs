using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Helpers;
using Lumen.App.Services;
using Lumen.Core;
using Lumen.Core.Batch;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Settings;
using Lumen.Core.Upscaling;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.ViewModels;

/// <summary>
/// v2 视图模型：左侧批处理队列 + 处理模式（自动/GPU/CPU 兼容）+ 批量输出设置 + 选中项画布。
/// 状态机：空队列 → 等待多张 → 批量处理中 → 完成/失败（可对比）→ 可重新处理。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>单张选中项预览的长边上限（画布显示用）。</summary>
    public const int PreviewMaxDimension = 2560;

    /// <summary>批处理结果预览的长边上限（队列对比用，省内存）。</summary>
    public const int BatchPreviewMaxDimension = 1600;

    /// <summary>
    /// 最多保留在内存里的结果预览张数，其余从输出文件懒加载。
    /// 迭代 3：8 → 4（每张 1600 长边预览约 4–6 MB，4 张足够当前/最近浏览，切换时按需从磁盘加载）。
    /// </summary>
    public const int MaxCachedResultPreviews = 4;

    /// <summary>缩略图边长。</summary>
    public const int ThumbnailSize = 96;

    /// <summary>输出像素总数上限：1.5 亿 ≈ 600 MB RGBA。</summary>
    public const long MaxOutputPixels = 150_000_000;

    /// <summary>接近上限时的警告阈值（约 400 MB RGBA）。</summary>
    public const long WarningOutputPixels = 100_000_000;

    private static readonly string[] SupportedImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif",
    ];

    /// <summary>调试用：LUMEN_SIMULATE_BATCH=1 且批处理服务未就绪时，用计时器模拟批次（仅 UI 截图验证）。</summary>
    /// <summary>调试用环境变量是否覆盖了对应设置（覆盖值不应写回用户设置，避免 QA/截图污染设置）。</summary>
    private static bool HasEnvironment(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static readonly bool SimulationEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LUMEN_SIMULATE_BATCH"), "1", StringComparison.Ordinal);

    /// <summary>调试用：LUMEN_PERF_LOG=1 时输出 [perf] 性能日志（用于优化前后对比）。</summary>
    private static readonly bool PerfLogging =
        string.Equals(Environment.GetEnvironmentVariable("LUMEN_PERF_LOG"), "1", StringComparison.Ordinal);

    private readonly AppServices _services;
    private readonly IImageCodec _codec;
    private readonly IModelCatalog _catalog;
    private readonly ISettingsStore _settingsStore;
    private readonly IComputeEnvironment _compute;
    private readonly FileDialogService _fileDialogs;
    private readonly AppSettings _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _infoBarTimer;
    // 缩略图解码串行队列：最多 1 张同时解码，降低大图批量添加时的瞬时内存峰值（解码在后台线程）。
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly Stopwatch _thumbnailWatch = new();
    private int _thumbnailTotal;
    private int _thumbnailDone;
    private int _batchProgressCount;

    private CancellationTokenSource? _batchCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private string? _lastOutputDirectory;
    private bool _isInitialized;

    public MainViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _codec = services.Codec;
        _catalog = services.Catalog;
        _settingsStore = services.SettingsStore;
        _compute = services.Compute;
        _fileDialogs = services.FileDialogs;
        _settings = services.Settings;

        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("MainViewModel 必须在 UI 线程创建。");
        _infoBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _infoBarTimer.Tick += (_, _) => CloseInfoBar();

        // partial property 不能带字段初始化器，默认值统一在构造器里赋值。
        Zoom = 1.0;
        ZoomText = "100%";
        BackendName = _services.DirectMlAvailable ? "DirectML" : "CPU";
        DeviceName = _services.DirectMlAvailable
            ? _compute.PrimaryGpuName ?? "默认 GPU"
            : _compute.CpuName ?? "本地处理器";
        StatusText = "就绪";
        StatusDetail = "添加图片开始批处理";
        DimensionsText = "未载入图片";
        SelectedModelIndex = -1;
        ScaleIndex = 2;
        FormatIndex = 0;
        Quality = 92;
        PreserveAlpha = true;
        TileSize = 0;
        TilePadding = 0;
        CpuThreads = 0;
        FileNamePattern = "{name}_{scale}x";
        ConflictPolicyIndex = 0;
        OpenOutputFolderWhenDone = false;
        OutputDirectoryModeIndex = 0;
        CustomOutputDirectory = string.Empty;
        ModelDescription = string.Empty;
        ModelTierLabel = string.Empty;
        ModelSizeText = "—";
        InputSizeText = "—";
        OutputSizeText = "—";
        IsPanelExpanded = true;
        InfoBarTitle = string.Empty;
        InfoBarMessage = string.Empty;

        InitializeFromSettings();
        ApplyDebugEnvironmentOverrides();
        InitializeDirectMlFallback();
        UpdateBackendLabelsForMode();

        if (Models.Count == 0)
        {
            ShowInfoBar(
                InfoBarSeverity.Error,
                "未找到可用模型",
                "模型目录为空或缺少有效的 model.json；可在系统信息里查看模型目录路径。",
                autoClose: false);
        }

        _isInitialized = true;
        UpdateQueueStatistics();
    }

    /// <summary>请求把画布缩放到“适应窗口”（视图负责真正的计算，因为视口尺寸在控件里）。</summary>
    public event EventHandler? FitRequested;

    // ───────────── 队列 ─────────────

    public ObservableCollection<QueueItem> Queue { get; } = [];

    [ObservableProperty]
    public partial QueueItem? SelectedQueueItem { get; set; }

    [ObservableProperty]
    public partial bool IsQueueCollapsed { get; set; }

    [ObservableProperty]
    public partial int QueueCount { get; set; }

    [ObservableProperty]
    public partial int CompletedCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    public bool IsEmptyState => Queue.Count == 0;

    public bool HasQueueItems => Queue.Count > 0;

    public string QueueSummary => Queue.Count == 0
        ? "队列为空"
        : $"{Queue.Count} 张 · 已完成 {CompletedCount} / 失败 {FailedCount}";

    // ───────────── 绘制/选中项 ─────────────

    [ObservableProperty]
    public partial ImageSource? OriginalImage { get; set; }

    [ObservableProperty]
    public partial ImageSource? ResultImage { get; set; }

    [ObservableProperty]
    public partial int ImagePixelWidth { get; set; }

    [ObservableProperty]
    public partial int ImagePixelHeight { get; set; }

    [ObservableProperty]
    public partial bool HasImage { get; set; }

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial bool IsCompareMode { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingPreview { get; set; }

    [ObservableProperty]
    public partial double Zoom { get; set; }

    [ObservableProperty]
    public partial string ZoomText { get; set; }

    // ───────────── 处理中 ─────────────

    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string BatchProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string StatusDetail { get; set; }

    [ObservableProperty]
    public partial string BackendName { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial string DimensionsText { get; set; }

    // ───────────── 处理模式 ─────────────

    [ObservableProperty]
    public partial int ComputeModeIndex { get; set; }

    public ComputeMode ComputeModeValue => ComputeModeIndex switch
    {
        1 => ComputeMode.Gpu,
        2 => ComputeMode.CpuCompat,
        _ => ComputeMode.Auto,
    };

    public string ComputeModeLabel => ComputeModeValue switch
    {
        ComputeMode.Gpu => "GPU",
        ComputeMode.CpuCompat => "CPU 兼容",
        _ => "自动",
    };

    public string ComputeModeDescription => ComputeModeValue switch
    {
        ComputeMode.Gpu => "强制 DirectML GPU；失败时直接报错，不会静默回退。",
        ComputeMode.CpuCompat => "CPU fp32 推理，兼容性最好，速度较慢。",
        _ => _services.DirectMlAvailable
            ? "优先使用 DirectML GPU，会话创建失败时由引擎回退 CPU。"
            : "未检测到 DirectML，将使用 CPU 兼容模式。",
    };

    /// <summary>右侧面板下方小字：当前模式对应的实际设备。</summary>
    public string DeviceSummary => ComputeModeValue switch
    {
        ComputeMode.Gpu => $"DirectML · {_compute.PrimaryGpuName ?? "默认 GPU"}",
        ComputeMode.CpuCompat => $"CPU · {_compute.CpuName ?? "本地处理器"}",
        _ => _services.DirectMlAvailable
            ? $"DirectML · {_compute.PrimaryGpuName ?? "默认 GPU"}"
            : $"CPU · {_compute.CpuName ?? "本地处理器"}",
    };

    public ExecutionBackend EffectiveBackend => ComputeModeValue switch
    {
        ComputeMode.Gpu => ExecutionBackend.DirectML,
        ComputeMode.CpuCompat => ExecutionBackend.Cpu,
        _ => ExecutionBackend.Auto,
    };

    public bool EffectivePreferFp16 => ComputeModeValue != ComputeMode.CpuCompat;

    public bool EffectiveCpuCompatibilityMode => ComputeModeValue == ComputeMode.CpuCompat;

    // ───────────── 模型 / 参数 ─────────────

    public ObservableCollection<ModelOption> Models { get; } = [];

    [ObservableProperty]
    public partial int SelectedModelIndex { get; set; }

    [ObservableProperty]
    public partial string ModelDescription { get; set; }

    [ObservableProperty]
    public partial string ModelTierLabel { get; set; }

    [ObservableProperty]
    public partial string ModelSizeText { get; set; }

    [ObservableProperty]
    public partial bool ShowGeneralModels { get; set; }

    [ObservableProperty]
    public partial int ScaleIndex { get; set; }

    [ObservableProperty]
    public partial int FormatIndex { get; set; }

    [ObservableProperty]
    public partial double Quality { get; set; }

    [ObservableProperty]
    public partial bool PreserveAlpha { get; set; }

    [ObservableProperty]
    public partial double TileSize { get; set; }

    [ObservableProperty]
    public partial double TilePadding { get; set; }

    [ObservableProperty]
    public partial double CpuThreads { get; set; }

    // ───────────── 批量输出 ─────────────

    /// <summary>0 = 源文件所在目录；1 = 自定义目录。</summary>
    [ObservableProperty]
    public partial int OutputDirectoryModeIndex { get; set; }

    [ObservableProperty]
    public partial string CustomOutputDirectory { get; set; }

    [ObservableProperty]
    public partial string FileNamePattern { get; set; }

    [ObservableProperty]
    public partial int ConflictPolicyIndex { get; set; }

    [ObservableProperty]
    public partial bool OpenOutputFolderWhenDone { get; set; }

    public bool UseCustomOutputDirectory => OutputDirectoryModeIndex == 1;

    public string OutputDirectoryDisplay => !UseCustomOutputDirectory
        ? "源文件所在目录（每张图旁边）"
        : string.IsNullOrWhiteSpace(CustomOutputDirectory)
            ? "尚未选择目录，点击“选择文件夹…”" 
            : CustomOutputDirectory;

    public OutputConflictPolicy ConflictPolicyValue => ConflictPolicyIndex switch
    {
        1 => OutputConflictPolicy.Overwrite,
        2 => OutputConflictPolicy.Skip,
        _ => OutputConflictPolicy.AutoRename,
    };

    // ───────────── 选中项信息 ─────────────

    [ObservableProperty]
    public partial string InputSizeText { get; set; }

    [ObservableProperty]
    public partial string OutputSizeText { get; set; }

    [ObservableProperty]
    public partial bool OutputSizeExceeded { get; set; }

    [ObservableProperty]
    public partial bool OutputSizeWarning { get; set; }

    // ───────────── 面板 / InfoBar ─────────────

    [ObservableProperty]
    public partial bool IsPanelExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity InfoBarSeverityValue { get; set; }

    [ObservableProperty]
    public partial string InfoBarTitle { get; set; }

    [ObservableProperty]
    public partial string InfoBarMessage { get; set; }

    [ObservableProperty]
    public partial bool CanOpenOutputFolder { get; set; }

    // ───────────── 派生属性 ─────────────

    public bool CanAdd => !IsProcessing;

    public bool CanClearQueue => !IsProcessing && Queue.Count > 0;

    public bool CanStartOrCancel => IsProcessing
        || (Queue.Count > 0 && SelectedModel is not null && !OutputSizeExceeded);

    public bool CanZoom => HasImage;

    public bool IsPanelEnabled => !IsProcessing;

    public bool IsEngineAvailable => _services.Upscaler is not null;

    public bool IsBatchAvailable => _services.BatchUpscaler is not null || SimulationEnabled;

    public string StartButtonText => IsProcessing
        ? "取消"
        : Queue.Count > 1 ? "开始处理全部" : "开始超分";

    public ModelOption? SelectedModel =>
        SelectedModelIndex >= 0 && SelectedModelIndex < Models.Count ? Models[SelectedModelIndex] : null;

    public OutputFormat SelectedOutputFormat => FormatIndex switch
    {
        1 => OutputFormat.Jpeg,
        2 => OutputFormat.WebP,
        _ => OutputFormat.Png,
    };

    public int Scale => ScaleIndex + 2;

    public bool ShowQuality => FormatIndex is 1 or 2;

    public bool PreserveAlphaEnabled => FormatIndex is 0 or 2;

    public string QualityLabel => Ui.Number(Quality);

    public bool ShowScaleNote => SelectedModel is { } model && Scale < model.Model.Scale;

    public string ScaleNoteText =>
        SelectedModel is { } model && Scale < model.Model.Scale
            ? $"由 {model.Model.Scale}× 输出高质量降采样"
            : string.Empty;

    public string ZoomPercentText => ZoomText;

    // ───────────── 命令：队列 ─────────────

    [RelayCommand]
    private async Task AddImagesAsync()
    {
        if (!CanAdd)
        {
            return;
        }

        IReadOnlyList<string> paths = await _fileDialogs.PickImagesAsync();
        if (paths.Count > 0)
        {
            await AddPathsAsync(paths);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (!CanAdd)
        {
            return;
        }

        string? folder = await _fileDialogs.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await AddPathsAsync([folder]);
        }
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (!CanClearQueue)
        {
            return;
        }

        foreach (QueueItem item in Queue)
        {
            item.PropertyChanged -= OnQueueItemPropertyChanged;
        }

        Queue.Clear();
        SelectedQueueItem = null;
        UpdateQueueStatistics();
        StatusText = "队列已清空";
        StatusDetail = "添加图片开始批处理";
        ClearCanvas();
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        if (IsProcessing)
        {
            CancelBatch();
            return;
        }

        await StartBatchAsync(null);
    }

    [RelayCommand]
    private void Cancel() => CancelBatch();

    [RelayCommand]
    private void ToggleQueuePanel() => IsQueueCollapsed = !IsQueueCollapsed;

    [RelayCommand]
    private void TogglePanel() => IsPanelExpanded = !IsPanelExpanded;

    // ───────────── 命令：画布 ─────────────

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Zoom100() => SetZoom(1.0);

    [RelayCommand]
    private void ZoomIn() => SetZoom(Zoom * 1.25);

    [RelayCommand]
    private void ZoomOut() => SetZoom(Zoom / 1.25);

    // ───────────── 命令：批量输出 / 系统 ─────────────

    [RelayCommand]
    private async Task BrowseOutputDirectoryAsync()
    {
        string? folder = await _fileDialogs.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        CustomOutputDirectory = folder;
        OutputDirectoryModeIndex = 1;
        OnPropertyChanged(nameof(UseCustomOutputDirectory));
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
    }

    [RelayCommand]
    private void ClearOutputDirectory()
    {
        CustomOutputDirectory = string.Empty;
        OutputDirectoryModeIndex = 0;
        OnPropertyChanged(nameof(UseCustomOutputDirectory));
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        string? directory = _lastOutputDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = UseCustomOutputDirectory ? CustomOutputDirectory : null;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            QueueItem? completed = Queue.FirstOrDefault(item => item.HasOutput && !string.IsNullOrWhiteSpace(item.OutputPath));
            if (completed?.OutputPath is { } outputPath)
            {
                directory = Path.GetDirectoryName(outputPath);
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _settings.LastBatchInputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenFolder(directory);
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(Path.GetDirectoryName(AppLog.LogFile) ?? AppPaths.LogsDirectory);

    [RelayCommand]
    private void OpenModelsFolder() => OpenFolder(_catalog.ModelsRoot);

    [RelayCommand]
    private void DismissInfoBar() => CloseInfoBar();

    // ───────────── 队列项操作（由 QueueItem 命令调用） ─────────────

    internal void RemoveItem(QueueItem item)
    {
        if (IsProcessing)
        {
            ShowInfoBar(InfoBarSeverity.Warning, "正在处理", "请等待当前批次结束或取消后再移除。", autoClose: true);
            return;
        }

        item.PropertyChanged -= OnQueueItemPropertyChanged;
        Queue.Remove(item);
        if (ReferenceEquals(SelectedQueueItem, item))
        {
            SelectedQueueItem = Queue.FirstOrDefault();
        }

        UpdateQueueStatistics();
    }

    internal void ReprocessItem(QueueItem item)
    {
        if (IsProcessing)
        {
            ShowInfoBar(InfoBarSeverity.Warning, "正在处理", "请等待当前批次结束或取消后再重新处理。", autoClose: true);
            return;
        }

        item.MarkPending();
        UpdateQueueStatistics();
        if (!ReferenceEquals(SelectedQueueItem, item))
        {
            SelectedQueueItem = item;
        }

        _ = StartBatchAsync([item]);
    }

    internal void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开文件失败：{ex.Message}");
        }
    }

    internal void RevealFileInFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开文件夹失败：{ex.Message}");
        }
    }

    internal void OpenFolder(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开文件夹失败：{ex.Message}");
        }
    }

    // ───────────── 添加图片 ─────────────

    /// <summary>添加图片/文件夹到队列（文件选择器、拖放、LUMEN_OPEN* 调试变量共用）。</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (IsProcessing)
        {
            ShowInfoBar(InfoBarSeverity.Warning, "正在处理", "请等待当前批次结束或取消后再添加图片。", autoClose: true);
            return;
        }

        var addWatch = Stopwatch.StartNew();
        var existing = new HashSet<string>(Queue.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        string? lastInputDirectory = null;
        int missingPaths = 0;

        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(raw))
                {
                    lastInputDirectory = raw;
                    // 大目录递归扫描放到后台线程，避免添加文件夹时 UI 线程卡顿。
                    List<string> found = await Task.Run(() =>
                    {
                        var collected = new List<string>();
                        CollectImageFiles(raw, collected);
                        return collected;
                    });
                    foreach (string file in found)
                    {
                        if (existing.Add(file))
                        {
                            files.Add(file);
                        }
                    }
                }
                else if (File.Exists(raw))
                {
                    string full = Path.GetFullPath(raw);
                    if (existing.Add(full))
                    {
                        files.Add(full);
                    }
                }
                else
                {
                    missingPaths++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Warn($"添加路径失败：{raw}：{ex.Message}");
            }
        }

        foreach (string file in files)
        {
            var item = new QueueItem(this, file);
            item.MarkPending();
            item.PropertyChanged += OnQueueItemPropertyChanged;
            Queue.Add(item);
            _ = LoadThumbnailAsync(item);
        }

        if (files.Count > 0)
        {
            _thumbnailTotal = files.Count;
            _thumbnailDone = 0;
            _thumbnailWatch.Restart();
            Perf($"addpaths count={files.Count} enum={addWatch.ElapsedMilliseconds}ms queue={Queue.Count}");
            UpdateQueueStatistics();
            if (!string.IsNullOrWhiteSpace(lastInputDirectory))
            {
                _settings.LastBatchInputDirectory = lastInputDirectory;
            }

            SelectedQueueItem ??= Queue[0];
            StatusText = $"已添加 {files.Count} 张";
            StatusDetail = $"队列共 {Queue.Count} 张";
        }
        else if (missingPaths > 0)
        {
            StatusText = "路径不存在或不可用";
            StatusDetail = $"有 {missingPaths} 个路径不存在或不是有效文件";
        }
        else
        {
            StatusText = "没有新增图片";
            StatusDetail = "文件重复或格式不受支持";
        }

        if (missingPaths > 0)
        {
            ShowInfoBar(
                InfoBarSeverity.Warning,
                "部分路径不可用",
                $"有 {missingPaths} 个路径不存在或不是有效文件。",
                autoClose: true);
        }

        await Task.CompletedTask;
    }

    private static bool IsSupportedImage(string path) =>
        SupportedImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static void CollectImageFiles(string directory, List<string> results, int depth = 0)
    {
        if (depth > 12)
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (IsSupportedImage(file))
                {
                    results.Add(Path.GetFullPath(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"扫描目录失败：{directory}：{ex.Message}");
        }

        try
        {
            foreach (string subdirectory in Directory.EnumerateDirectories(directory))
            {
                CollectImageFiles(subdirectory, results, depth + 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 子目录不可访问时跳过。
        }
    }

    private async Task LoadThumbnailAsync(QueueItem item)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            // 迭代 3：优先走编码器缩放解码（JPEG 采样 1/2、1/4、1/8），避免为 96px 缩略图解码整张大图。
            IImageThumbnailLoader? thumbnailLoader = _services.ThumbnailLoader;
            ImagePixels preview = await Task.Run(() =>
            {
                if (thumbnailLoader is not null)
                {
                    return thumbnailLoader.LoadThumbnail(item.Path, ThumbnailSize);
                }

                LoadedImage loaded = _codec.Load(item.Path);
                return _codec.CreatePreview(loaded.Pixels, ThumbnailSize, ThumbnailSize);
            });

            item.Thumbnail = ImageDisplayService.CreateBitmap(preview);
        }
        catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or OutOfMemoryException
                                       or ArgumentException)
        {
            AppLog.Warn($"缩略图加载失败：{item.Path}：{ex.Message}");
        }
        finally
        {
            _thumbnailGate.Release();
            if (PerfLogging && _thumbnailTotal > 0)
            {
                int done = Interlocked.Increment(ref _thumbnailDone);
                if (done >= _thumbnailTotal)
                {
                    Perf($"thumbnails ready count={_thumbnailTotal} elapsed={_thumbnailWatch.ElapsedMilliseconds}ms " + MemoryText());
                }
            }
        }
    }

    private void OnQueueItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItem.HasOutput) or nameof(QueueItem.IsFailed) or nameof(QueueItem.IsProcessing))
        {
            UpdateQueueStatistics();
        }
    }

    private void UpdateQueueStatistics()
    {
        QueueCount = Queue.Count;
        CompletedCount = Queue.Count(item => item.HasOutput);
        FailedCount = Queue.Count(item => item.IsFailed);

        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(IsEmptyState));
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanClearQueue));
        OnPropertyChanged(nameof(CanStartOrCancel));
        OnPropertyChanged(nameof(StartButtonText));
    }

    // ───────────── 选中项 / 画布 ─────────────

    partial void OnSelectedQueueItemChanged(QueueItem? value) => RefreshSelectedItem(value);

    private async void RefreshSelectedItem(QueueItem? item)
    {
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _selectionCancellation = cancellation;

        ClearCanvas();
        if (item is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        IsLoadingPreview = true;
        try
        {
            // 结果预览（已完成项）：优先用批量服务返回的内存预览，其次从输出文件懒加载。
            if (item.HasOutput)
            {
                ImagePixels? resultPixels = await EnsureResultPreviewAsync(item, cancellation.Token);
                if (!cancellation.IsCancellationRequested && resultPixels is not null)
                {
                    byte[] resultBgra = await Task.Run(
                        () => ImageDisplayService.PrepareBgraPremultiplied(resultPixels), cancellation.Token);
                    ResultImage = ImageDisplayService.CreateBitmap(resultPixels.Width, resultPixels.Height, resultBgra);
                    HasResult = true;
                    IsCompareMode = true;
                }
            }

            // 原始图预览。
            var loaded = await Task.Run(() =>
            {
                LoadedImage image = _codec.Load(item.Path);
                ImagePixels preview = _codec.CreatePreview(image.Pixels, PreviewMaxDimension, PreviewMaxDimension);
                // BGRA 预乘转换在后台线程完成，UI 线程只写 PixelBuffer。
                byte[] bgra = ImageDisplayService.PrepareBgraPremultiplied(preview);
                return (Bgra: bgra, PreviewWidth: preview.Width, PreviewHeight: preview.Height, image.Width, image.Height);
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            item.InputWidth = loaded.Width;
            item.InputHeight = loaded.Height;
            OriginalImage = ImageDisplayService.CreateBitmap(loaded.PreviewWidth, loaded.PreviewHeight, loaded.Bgra);
            ImagePixelWidth = loaded.Width;
            ImagePixelHeight = loaded.Height;
            HasImage = true;

            UpdatePanelEstimates();
            UpdateDimensionsText();
            FitRequested?.Invoke(this, EventArgs.Empty);

            if (!HasResult)
            {
                IsCompareMode = false;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户快速切换选中项，忽略。
        }
        catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or OutOfMemoryException
                                       or ArgumentException)
        {
            AppLog.Error($"无法预览图片：{item.Path}", ex);
            StatusText = "无法预览";
            StatusDetail = item.FileName;
            ShowInfoBar(InfoBarSeverity.Error, "无法预览图片", $"{item.FileName}：{Ui.UserMessage(ex)}", autoClose: false);
        }
        finally
        {
            if (ReferenceEquals(_selectionCancellation, cancellation))
            {
                IsLoadingPreview = false;
            }
        }
    }

    private async Task<ImagePixels?> EnsureResultPreviewAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (item.ResultPreview is not null)
        {
            return item.ResultPreview;
        }

        if (string.IsNullOrWhiteSpace(item.OutputPath) || !File.Exists(item.OutputPath))
        {
            return null;
        }

        try
        {
            ImagePixels preview = await Task.Run(() =>
            {
                LoadedImage loaded = _codec.Load(item.OutputPath!);
                return _codec.CreatePreview(loaded.Pixels, PreviewMaxDimension, PreviewMaxDimension);
            }, cancellationToken);

            item.ResultPreview = preview;
            TrimResultPreviewCache(preferred: item);
            return preview;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException)
        {
            AppLog.Warn($"加载结果预览失败：{item.OutputPath}：{ex.Message}");
            return null;
        }
    }

    private void TrimResultPreviewCache(QueueItem? preferred = null)
    {
        List<QueueItem> cached = Queue.Where(item => item.ResultPreview is not null).ToList();
        if (cached.Count <= MaxCachedResultPreviews)
        {
            return;
        }

        int excess = cached.Count - MaxCachedResultPreviews;
        foreach (QueueItem item in cached)
        {
            if (excess <= 0)
            {
                break;
            }

            if (ReferenceEquals(item, preferred) || ReferenceEquals(item, SelectedQueueItem))
            {
                continue;
            }

            item.ResultPreview = null;
            excess--;
        }
    }

    private void ClearCanvas()
    {
        OriginalImage = null;
        ResultImage = null;
        HasImage = false;
        HasResult = false;
        IsCompareMode = false;
        ImagePixelWidth = 0;
        ImagePixelHeight = 0;
        UpdatePanelEstimates();
        UpdateDimensionsText();
    }

    // ───────────── 批处理 ─────────────

    /// <summary>开始处理：不传 <paramref name="subset"/> 时处理队列中未完成的图片。</summary>
    public async Task StartBatchAsync(IReadOnlyList<QueueItem>? subset)
    {
        if (IsProcessing)
        {
            return;
        }

        List<QueueItem> items = (subset ?? Queue.Where(item => !item.HasOutput).ToList()).ToList();
        if (items.Count == 0)
        {
            ShowInfoBar(InfoBarSeverity.Informational, "没有待处理图片", "队列中的图片都已处理完成；可在右键菜单里重新处理。", autoClose: true);
            return;
        }

        if (SelectedModel is not { } model)
        {
            ShowInfoBar(InfoBarSeverity.Error, "没有可用模型", "models 目录中没有找到有效模型。", autoClose: false);
            return;
        }

        if (ComputeModeValue == ComputeMode.Gpu && !_services.DirectMlAvailable)
        {
            ShowInfoBar(
                InfoBarSeverity.Error,
                "未检测到 DirectML GPU",
                "当前处理模式为“GPU”，请切换到“自动”或“CPU 兼容”后重试。",
                autoClose: false);
            return;
        }

        if (!BuildBatchOptions(model, out BatchOptions? options, out string? error))
        {
            ShowInfoBar(InfoBarSeverity.Warning, "输出设置无效", error ?? "请检查批量输出设置。", autoClose: false);
            return;
        }

        bool useSimulation = _services.BatchUpscaler is null;
        if (useSimulation && !SimulationEnabled)
        {
            ShowInfoBar(
                InfoBarSeverity.Error,
                "批处理服务不可用",
                "引擎侧的批处理服务尚未就绪，请稍后重试（单张预览仍可用）。",
                autoClose: false);
            return;
        }

        foreach (QueueItem item in items)
        {
            item.MarkPending();
        }

        UpdateQueueStatistics();
        IsProcessing = true;
        ProgressValue = 0;
        BatchProgressText = $"共 {items.Count} 张";
        StatusText = "开始处理";
        StatusDetail = $"共 {items.Count} 张 · {ComputeModeLabel}";
        BackendName = ComputeModeValue == ComputeMode.CpuCompat ? "CPU" : _services.DirectMlAvailable ? "DirectML" : "CPU";
        _batchCancellation = new CancellationTokenSource();
        _batchProgressCount = 0;
        Perf($"batch start items={items.Count} mode={ComputeModeLabel} backend={EffectiveBackend} " + MemoryText());
        // 迭代 3：进度节流——引擎每个 tile 回调一次，这里按“阶段变化 / ≥1% / ≥100ms”合并后再 Post 到 UI 线程。
        var progress = new ThrottledBatchProgress(_dispatcher, OnBatchProgress);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            IReadOnlyList<BatchItemResult> results = useSimulation
                ? await SimulateBatchAsync(items, progress, _batchCancellation.Token)
                : await _services.BatchUpscaler!.ProcessAsync(
                    items.Select(item => item.Path).ToList(),
                    options!,
                    progress,
                    _batchCancellation.Token);

            stopwatch.Stop();
            ApplyBatchResults(items, results, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("用户取消了批处理。");
            ApplyCanceledResults(items);
            StatusText = "已取消";
            StatusDetail = $"成功 {items.Count(item => item.HasOutput)} / 失败 {items.Count(item => item.IsFailed)}";
        }
        catch (Exception ex)
        {
            AppLog.Error("批处理失败。", ex);
            HandleBatchFailure(items, ex);
        }
        finally
        {
            stopwatch.Stop();
            _batchCancellation?.Dispose();
            _batchCancellation = null;
            IsProcessing = false;
            ProgressValue = 0;
            BatchProgressText = string.Empty;
            UpdateQueueStatistics();
        }
    }

    private bool BuildBatchOptions(ModelOption model, out BatchOptions? options, out string? error)
    {
        options = null;
        error = null;

        string pattern = string.IsNullOrWhiteSpace(FileNamePattern) ? "{name}_{scale}x" : FileNamePattern.Trim();
        string? outputDirectory = UseCustomOutputDirectory && !string.IsNullOrWhiteSpace(CustomOutputDirectory)
            ? CustomOutputDirectory.Trim()
            : null;

        try
        {
            var upscale = new UpscaleOptions
            {
                Model = model.Model,
                TargetScale = Scale,
                Backend = EffectiveBackend,
                PreferFp16 = EffectivePreferFp16,
                TileSize = (int)Math.Round(TileSize),
                TilePadding = (int)Math.Round(TilePadding),
                PreserveAlpha = PreserveAlpha && SelectedOutputFormat != OutputFormat.Jpeg,
                PreviewMaxDimension = BatchPreviewMaxDimension,
                MaxOutputPixels = MaxOutputPixels,
                CpuThreads = (int)Math.Round(CpuThreads),
                CpuCompatibilityMode = EffectiveCpuCompatibilityMode,
            };

            var candidate = new BatchOptions
            {
                Upscale = upscale,
                Save = new SaveOptions(SelectedOutputFormat, (int)Math.Round(Quality)),
                OutputDirectory = outputDirectory,
                FileNamePattern = pattern,
                ConflictPolicy = ConflictPolicyValue,
                KeepPreview = true,
                PreviewMaxDimension = BatchPreviewMaxDimension,
            };

            candidate.Validate();
            options = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            error = Ui.UserMessage(ex);
            return false;
        }
    }

    private void OnBatchProgress(BatchProgress progress)
    {
        if (!IsProcessing)
        {
            return;
        }

        Interlocked.Increment(ref _batchProgressCount);

        QueueItem? item = Queue.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, progress.InputPath, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            switch (progress.Stage)
            {
                case BatchItemStage.Loading:
                    item.MarkLoading();
                    break;
                case BatchItemStage.Saving:
                    item.MarkSaving();
                    break;
                default:
                    item.MarkProcessing(progress.ItemFraction);
                    break;
            }
        }

        ProgressValue = Math.Clamp(progress.OverallFraction * 100.0, 0, 100);

        int index = progress.ItemIndex + 1;
        string tiles = progress.TilesTotal > 0
            ? $" · 当前块 {progress.TilesCompleted}/{progress.TilesTotal}"
            : string.Empty;
        BatchProgressText = $"第 {index} / {progress.ItemCount} 张 · 整体 {progress.OverallFraction * 100:0}%{tiles}";
        StatusText = progress.Stage switch
        {
            BatchItemStage.Loading => "读取图片",
            BatchItemStage.Saving => "保存结果",
            _ => "批量处理中",
        };
        StatusDetail = $"{BatchProgressText} · {BackendName}";
    }

    private void ApplyBatchResults(IReadOnlyList<QueueItem> items, IReadOnlyList<BatchItemResult> results, TimeSpan totalDuration)
    {
        int success = 0;
        int failed = 0;
        int skipped = 0;
        int canceled = 0;
        string? backend = null;
        string? outputDirectory = null;

        for (int index = 0; index < items.Count; index++)
        {
            QueueItem item = items[index];
            BatchItemResult? result = results.FirstOrDefault(candidate =>
                string.Equals(candidate.InputPath, item.Path, StringComparison.OrdinalIgnoreCase));

            if (result is null && index < results.Count)
            {
                result = results[index];
            }

            if (result is null)
            {
                item.MarkFailed("未返回处理结果。");
                failed++;
                continue;
            }

            switch (result.Status)
            {
                case BatchItemStatus.Completed:
                    item.MarkCompleted(result);
                    success++;
                    backend ??= result.Backend;
                    if (!string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        outputDirectory ??= Path.GetDirectoryName(result.OutputPath);
                    }

                    break;
                case BatchItemStatus.Failed:
                    item.MarkFailed(result.ErrorMessage ?? "处理失败。");
                    failed++;
                    break;
                case BatchItemStatus.Skipped:
                    item.MarkSkipped(result.OutputPath);
                    skipped++;
                    break;
                default:
                    item.MarkCanceled();
                    canceled++;
                    break;
            }
        }

        _lastOutputDirectory = outputDirectory
            ?? (UseCustomOutputDirectory && !string.IsNullOrWhiteSpace(CustomOutputDirectory) ? CustomOutputDirectory : null);

        if (!string.IsNullOrWhiteSpace(backend))
        {
            BackendName = backend;
            DeviceName = backend.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                ? _compute.CpuName ?? "本地处理器"
                : _compute.PrimaryGpuName ?? "默认 GPU";
        }

        UpdateQueueStatistics();
        TrimResultPreviewCache();

        if (SelectedQueueItem is { } selected && items.Contains(selected))
        {
            _ = RefreshSelectedResultAsync(selected);
        }

        StatusText = canceled > 0 ? "已取消" : failed > 0 ? "批量完成（有失败）" : "批量完成";
        StatusDetail = $"成功 {success} / 失败 {failed}"
                       + (skipped > 0 ? $" / 跳过 {skipped}" : string.Empty)
                       + (canceled > 0 ? $" / 取消 {canceled}" : string.Empty)
                       + $" · 用时 {Ui.Elapsed(totalDuration)}";

        InfoBarSeverity severity = failed > 0 || canceled > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ShowInfoBar(
            severity,
            canceled > 0 ? "批处理已取消" : "批量处理完成",
            canceled > 0
                ? $"成功 {success} / 失败 {failed} · 已取消 {canceled} 张"
                : $"成功 {success} / 失败 {failed}"
                  + (skipped > 0 ? $" / 跳过 {skipped}" : string.Empty)
                  + $" · 用时 {Ui.Elapsed(totalDuration)}",
            autoClose: false);

        // 有失败项时汇总 InfoBar 是 Warning，但仍要保留“打开输出文件夹”动作（规格 §5.2.4）。
        CanOpenOutputFolder = _lastOutputDirectory is not null;
        Perf($"batch done items={items.Count} success={success} failed={failed} skipped={skipped} " +
             $"duration={totalDuration.TotalMilliseconds:0}ms progressCallbacks={_batchProgressCount} " +
             $"resultPreviews={Queue.Count(item => item.ResultPreview is not null)} " + MemoryText());

        if (OpenOutputFolderWhenDone && _lastOutputDirectory is not null)
        {
            OpenFolder(_lastOutputDirectory);
        }
    }

    private async Task RefreshSelectedResultAsync(QueueItem item)
    {
        if (!ReferenceEquals(item, SelectedQueueItem) || !item.HasOutput)
        {
            return;
        }

        try
        {
            ImagePixels? preview = await EnsureResultPreviewAsync(item, CancellationToken.None);
            if (preview is null || !ReferenceEquals(item, SelectedQueueItem))
            {
                return;
            }

            byte[] bgra = await Task.Run(() => ImageDisplayService.PrepareBgraPremultiplied(preview));
            if (!ReferenceEquals(item, SelectedQueueItem))
            {
                return;
            }

            ResultImage = ImageDisplayService.CreateBitmap(preview.Width, preview.Height, bgra);
            HasResult = true;
            IsCompareMode = true;
            UpdateDimensionsText();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"刷新结果预览失败：{ex.Message}");
        }
    }

    private void ApplyCanceledResults(IReadOnlyList<QueueItem> items)
    {
        foreach (QueueItem item in items)
        {
            if (!item.HasOutput && !item.IsFailed)
            {
                item.MarkCanceled();
            }
        }

        UpdateQueueStatistics();
        ShowInfoBar(
            InfoBarSeverity.Warning,
            "批处理已取消",
            $"成功 {items.Count(item => item.HasOutput)} / 失败 {items.Count(item => item.IsFailed)} · 已取消 {items.Count(item => !item.HasOutput && !item.IsFailed)} 张",
            autoClose: false);
    }

    private void HandleBatchFailure(IReadOnlyList<QueueItem> items, Exception exception)
    {
        string message = Ui.UserMessage(exception);
        bool gpuFailure = ComputeModeValue == ComputeMode.Gpu;

        foreach (QueueItem item in items)
        {
            if (!item.HasOutput && !item.IsFailed)
            {
                item.MarkFailed(gpuFailure ? "GPU 推理失败。" : message);
            }
        }

        UpdateQueueStatistics();
        StatusText = "处理失败";
        StatusDetail = message;
        ShowInfoBar(
            InfoBarSeverity.Error,
            gpuFailure ? "GPU（DirectML）推理失败" : "批处理失败",
            gpuFailure
                ? $"{message} 建议切换到“CPU 兼容”模式后重试。"
                : message,
            autoClose: false);
    }

    /// <summary>批处理服务未就绪时的 UI 模拟（仅 LUMEN_SIMULATE_BATCH=1，用于截图验证）。</summary>
    private async Task<IReadOnlyList<BatchItemResult>> SimulateBatchAsync(
        IReadOnlyList<QueueItem> items,
        IProgress<BatchProgress> progress,
        CancellationToken cancellationToken)
    {
        var results = new List<BatchItemResult>(items.Count);
        var total = Stopwatch.StartNew();

        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueueItem item = items[index];
            var itemWatch = Stopwatch.StartNew();
            bool decoded = true;
            string? decodeError = null;

            try
            {
                await Task.Run(() => _codec.Load(item.Path), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                decoded = false;
                decodeError = Ui.UserMessage(ex);
            }

            if (decoded)
            {
                for (int step = 1; step <= 10; step++)
                {
                    await Task.Delay(120, cancellationToken);
                    double itemFraction = step / 10.0;
                    progress.Report(new BatchProgress
                    {
                        ItemIndex = index,
                        ItemCount = items.Count,
                        InputPath = item.Path,
                        Stage = BatchItemStage.Upscaling,
                        ItemFraction = itemFraction,
                        OverallFraction = (index + itemFraction) / items.Count,
                        TilesCompleted = step * 2,
                        TilesTotal = 20,
                        Elapsed = total.Elapsed,
                    });
                }
            }

            itemWatch.Stop();
            results.Add(new BatchItemResult
            {
                InputPath = item.Path,
                Status = decoded ? BatchItemStatus.Completed : BatchItemStatus.Failed,
                ErrorMessage = decodeError,
                OutputPath = null,
                InputWidth = item.InputWidth,
                InputHeight = item.InputHeight,
                Duration = itemWatch.Elapsed,
                Backend = "模拟批处理",
                Preview = null,
            });
        }

        return results;
    }

    /// <summary>供窗口关闭确认调用：取消当前批处理。</summary>
    public void CancelProcessing() => CancelBatch();

    private void CancelBatch()
    {
        if (!IsProcessing)
        {
            return;
        }

        StatusText = "正在取消";
        StatusDetail = "等待当前分块结束…";
        try
        {
            _batchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 处理刚好结束，忽略。
        }
    }

    // ───────────── 初始化 / 设置 ─────────────

    private void InitializeFromSettings()
    {
        ShowGeneralModels = _settings.ShowGeneralModels;
        SelectedModelIndex = -1;
        RebuildModelList(preferredModelId: _settings.SelectedModelId);

        ScaleIndex = Math.Clamp(_settings.Scale - 2, 0, 2);
        FormatIndex = _settings.OutputFormat switch
        {
            OutputFormat.Jpeg => 1,
            OutputFormat.WebP => 2,
            _ => 0,
        };
        Quality = _settings.JpegQuality is >= 1 and <= 100 ? _settings.JpegQuality : 92;
        PreserveAlpha = _settings.PreserveAlpha;
        TileSize = _settings.TileSize;
        TilePadding = _settings.TilePadding;
        CpuThreads = _settings.CpuThreads;
        ComputeModeIndex = (int)_settings.ComputeMode;
        FileNamePattern = string.IsNullOrWhiteSpace(_settings.BatchFileNamePattern)
            ? "{name}_{scale}x"
            : _settings.BatchFileNamePattern;
        ConflictPolicyIndex = _settings.BatchConflictPolicy switch
        {
            OutputConflictPolicy.Overwrite => 1,
            OutputConflictPolicy.Skip => 2,
            _ => 0,
        };
        OpenOutputFolderWhenDone = _settings.OpenOutputFolderWhenDone;
        if (!string.IsNullOrWhiteSpace(_settings.BatchOutputDirectory))
        {
            OutputDirectoryModeIndex = 1;
            CustomOutputDirectory = _settings.BatchOutputDirectory;
        }
    }

    /// <summary>LUMEN_BACKEND / LUMEN_CONFLICT / LUMEN_PATTERN / LUMEN_SAVE_DIR / LUMEN_SAVE_TO 调试覆盖。</summary>
    private void ApplyDebugEnvironmentOverrides()
    {
        string? backend = Environment.GetEnvironmentVariable("LUMEN_BACKEND");
        if (!string.IsNullOrWhiteSpace(backend))
        {
            ComputeModeIndex = backend.Trim().ToLowerInvariant() switch
            {
                "gpu" or "dml" or "directml" => (int)ComputeMode.Gpu,
                "cpu" => (int)ComputeMode.CpuCompat,
                _ => (int)ComputeMode.Auto,
            };
        }

        string? conflict = Environment.GetEnvironmentVariable("LUMEN_CONFLICT");
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            ConflictPolicyIndex = conflict.Trim().ToLowerInvariant() switch
            {
                "overwrite" => 1,
                "skip" => 2,
                _ => 0,
            };
        }

        string? pattern = Environment.GetEnvironmentVariable("LUMEN_PATTERN");
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            FileNamePattern = pattern;
        }

        string? saveDirectory = Environment.GetEnvironmentVariable("LUMEN_SAVE_DIR");
        if (!string.IsNullOrWhiteSpace(saveDirectory))
        {
            CustomOutputDirectory = saveDirectory;
            OutputDirectoryModeIndex = 1;
        }

        string? saveTo = Environment.GetEnvironmentVariable("LUMEN_SAVE_TO");
        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            // 迭代 1 的单张输出路径调试变量：转换为“输出目录 + 文件名模板”。
            string full = Path.GetFullPath(saveTo);
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                CustomOutputDirectory = directory;
                OutputDirectoryModeIndex = 1;
                FileNamePattern = Path.GetFileNameWithoutExtension(full);
            }
        }

        OnPropertyChanged(nameof(UseCustomOutputDirectory));
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
    }

    private void InitializeDirectMlFallback()
    {
        if (ComputeModeValue == ComputeMode.Auto && !_services.DirectMlAvailable)
        {
            ComputeModeIndex = (int)ComputeMode.CpuCompat;
            ShowInfoBar(
                InfoBarSeverity.Warning,
                "未检测到可用的 DirectML GPU",
                "已切换到 CPU 兼容模式，速度较慢。",
                autoClose: false);
        }
    }

    /// <summary>处理模式变化时同步状态条左端的后端/设备标签（CPU 模式下必须显示 CPU + CPU 型号）。</summary>
    private void UpdateBackendLabelsForMode()
    {
        bool cpu = ComputeModeValue == ComputeMode.CpuCompat
                   || (ComputeModeValue == ComputeMode.Auto && !_services.DirectMlAvailable);
        BackendName = cpu ? "CPU" : "DirectML";
        DeviceName = cpu
            ? _compute.CpuName ?? "本地处理器"
            : _compute.PrimaryGpuName ?? "默认 GPU";
    }

    private void RebuildModelList(string? preferredModelId = null)
    {
        string? previousId = preferredModelId ?? SelectedModel?.Id;
        Models.Clear();

        List<ModelDescriptor> visible = _catalog.Models
            .Where(model => ShowGeneralModels || model.RecommendedFor.Contains(ModelCategory.Anime))
            .ToList();

        if (visible.Count == 0)
        {
            // 模型的 recommendedFor 还没跟上时不要让下拉框空掉。
            visible = _catalog.Models.ToList();
        }

        foreach (ModelDescriptor model in visible)
        {
            Models.Add(new ModelOption(model));
        }

        int index = 0;
        bool found = false;
        if (!string.IsNullOrWhiteSpace(previousId))
        {
            for (int i = 0; i < Models.Count; i++)
            {
                if (string.Equals(Models[i].Id, previousId, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    found = true;
                    break;
                }
            }
        }

        // 默认模型由 model.json 的 isDefault 决定（应落在最佳动漫模型上）；不可见时退回第一个可见模型。
        if (!found && _catalog.Default is { } defaultModel)
        {
            for (int i = 0; i < Models.Count; i++)
            {
                if (string.Equals(Models[i].Id, defaultModel.Id, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
        }

        SelectedModelIndex = Models.Count == 0 ? -1 : index;
        OnPropertyChanged(nameof(SelectedModel));
        UpdateModelInfo();
        OnPropertyChanged(nameof(CanStartOrCancel));
    }

    private void UpdateModelInfo()
    {
        ModelOption? option = SelectedModel;
        if (option is null)
        {
            ModelDescription = string.Empty;
            ModelTierLabel = string.Empty;
            ModelSizeText = "—";
            return;
        }

        ModelDescription = option.Description;
        ModelTierLabel = option.TierLabel;
        ModelSizeText = option.SizeLabel;
    }

    // ───────────── 系统信息（同时供对话框逐行绑定与“复制信息”） ─────────────

    public string SystemInfoVersion => $"Lumen {Ui.AppVersion()}";

    public string SystemInfoOs => $"{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）";

    public string SystemInfoRuntime => RuntimeInformation.FrameworkDescription;

    public string SystemInfoCpu => _compute.CpuName ?? "未知";

    public string SystemInfoGpu => _compute.GpuNames.Count > 0 ? string.Join(" / ", _compute.GpuNames) : "未检测到";

    public string SystemInfoDirectMl => _services.DirectMlAvailable ? "可用" : "不可用";

    public string SystemInfoModels => $"{_catalog.ModelsRoot}（{_catalog.Models.Count} 个模型，当前显示 {Models.Count} 个）";

    public string SystemInfoLogPath => AppLog.LogFile;

    public string SystemInfoBatchService => _services.BatchUpscaler is null
        ? (SimulationEnabled ? "模拟模式" : "不可用")
        : "就绪";

    /// <summary>系统信息文本（“复制信息”按钮使用）。</summary>
    public string BuildSystemInfoText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(SystemInfoVersion);
        builder.AppendLine($"系统：{SystemInfoOs}");
        builder.AppendLine($"运行时：{SystemInfoRuntime}");
        builder.AppendLine($"CPU：{SystemInfoCpu}");
        builder.AppendLine($"GPU：{SystemInfoGpu}");
        builder.AppendLine($"DirectML：{SystemInfoDirectMl}");
        builder.AppendLine($"模型目录：{SystemInfoModels}");
        builder.AppendLine($"批处理服务：{SystemInfoBatchService}");
        builder.AppendLine($"日志文件：{SystemInfoLogPath}");
        return builder.ToString();
    }

    /// <summary>退出前写回设置（窗口尺寸由 MainWindow 以 DIP 传入）。</summary>
    public void PersistSettings(int windowWidthDip, int windowHeightDip)
    {
        // LUMEN_* 调试覆盖（QA/截图）只影响本次运行，不写回用户设置。
        bool backendOverridden = HasEnvironment("LUMEN_BACKEND") || HasEnvironment("LUMEN_FORCE_NO_DML");
        bool scaleOverridden = HasEnvironment("LUMEN_SCALE");
        bool outputOverridden = HasEnvironment("LUMEN_SAVE_DIR") || HasEnvironment("LUMEN_SAVE_TO")
                                || HasEnvironment("LUMEN_PATTERN") || HasEnvironment("LUMEN_CONFLICT");

        _settings.SelectedModelId = SelectedModel?.Id;
        if (!scaleOverridden)
        {
            _settings.Scale = Scale;
        }

        if (!backendOverridden)
        {
            _settings.ComputeMode = ComputeModeValue;
            _settings.PreferFp16 = EffectivePreferFp16;
        }
        _settings.TileSize = (int)Math.Round(TileSize);
        _settings.TilePadding = (int)Math.Round(TilePadding);
        _settings.CpuThreads = (int)Math.Round(CpuThreads);
        _settings.ShowGeneralModels = ShowGeneralModels;
        _settings.OutputFormat = SelectedOutputFormat;
        _settings.JpegQuality = (int)Math.Round(Quality);
        _settings.WebpQuality = (int)Math.Round(Quality);
        _settings.PreserveAlpha = PreserveAlpha;
        if (!outputOverridden)
        {
            _settings.BatchOutputDirectory = UseCustomOutputDirectory && !string.IsNullOrWhiteSpace(CustomOutputDirectory)
                ? CustomOutputDirectory
                : null;
            _settings.BatchFileNamePattern = string.IsNullOrWhiteSpace(FileNamePattern) ? "{name}_{scale}x" : FileNamePattern.Trim();
            _settings.BatchConflictPolicy = ConflictPolicyValue;
        }
        _settings.OpenOutputFolderWhenDone = OpenOutputFolderWhenDone;
        if (windowWidthDip > 0 && windowHeightDip > 0)
        {
            _settings.WindowWidth = windowWidthDip;
            _settings.WindowHeight = windowHeightDip;
        }

        _settingsStore.Save(_settings);
    }

    // ───────────── 属性联动 ─────────────

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanClearQueue));
        OnPropertyChanged(nameof(CanStartOrCancel));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(IsPanelEnabled));
    }

    partial void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanZoom));
        OnPropertyChanged(nameof(CanStartOrCancel));
    }

    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(CanStartOrCancel));

    partial void OnOutputSizeExceededChanged(bool value) => OnPropertyChanged(nameof(CanStartOrCancel));

    partial void OnZoomChanged(double value)
    {
        ZoomText = Ui.Zoom(value);
        OnPropertyChanged(nameof(ZoomPercentText));
    }

    partial void OnScaleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ShowScaleNote));
        OnPropertyChanged(nameof(ScaleNoteText));
        UpdatePanelEstimates();
        UpdateDimensionsText();
    }

    partial void OnSelectedModelIndexChanged(int value)
    {
        UpdateModelInfo();
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(ShowScaleNote));
        OnPropertyChanged(nameof(ScaleNoteText));
        OnPropertyChanged(nameof(CanStartOrCancel));
        UpdatePanelEstimates();
    }

    partial void OnShowGeneralModelsChanged(bool value) => RebuildModelList();

    partial void OnFormatIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedOutputFormat));
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(PreserveAlphaEnabled));
    }

    partial void OnQualityChanged(double value) => OnPropertyChanged(nameof(QualityLabel));

    partial void OnComputeModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ComputeModeValue));
        OnPropertyChanged(nameof(ComputeModeLabel));
        OnPropertyChanged(nameof(ComputeModeDescription));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(EffectiveBackend));
        OnPropertyChanged(nameof(EffectivePreferFp16));
        OnPropertyChanged(nameof(EffectiveCpuCompatibilityMode));
        UpdateBackendLabelsForMode();

        if (_isInitialized && ComputeModeValue == ComputeMode.Gpu && !_services.DirectMlAvailable)
        {
            ShowInfoBar(
                InfoBarSeverity.Warning,
                "未检测到 DirectML GPU",
                "“GPU”模式需要可用的 DirectML 设备；开始处理前请切换为“自动”或“CPU 兼容”。",
                autoClose: false);
        }
    }

    partial void OnOutputDirectoryModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UseCustomOutputDirectory));
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
    }

    partial void OnCustomOutputDirectoryChanged(string value) =>
        OnPropertyChanged(nameof(OutputDirectoryDisplay));

    partial void OnIsInfoBarOpenChanged(bool value)
    {
        if (!value)
        {
            _infoBarTimer.Stop();
        }
    }

    // ───────────── 辅助 ─────────────

    /// <summary>更新面板底部与状态条右侧的尺寸/容量信息。</summary>
    private void UpdatePanelEstimates()
    {
        if (!HasImage || ImagePixelWidth <= 0)
        {
            InputSizeText = "—";
            OutputSizeText = "—";
            OutputSizeExceeded = false;
            OutputSizeWarning = false;
            return;
        }

        InputSizeText = $"{ImagePixelWidth} × {ImagePixelHeight}";

        long outputWidth = (long)ImagePixelWidth * Scale;
        long outputHeight = (long)ImagePixelHeight * Scale;
        bool exceeded = outputWidth * outputHeight > MaxOutputPixels;

        OutputSizeText = exceeded ? "超出上限" : $"{outputWidth} × {outputHeight}";
        OutputSizeExceeded = exceeded;
        OutputSizeWarning = !exceeded && outputWidth * outputHeight >= WarningOutputPixels;
    }

    private void UpdateDimensionsText()
    {
        if (!HasImage)
        {
            DimensionsText = "未载入图片";
            return;
        }

        long outputWidth = (long)ImagePixelWidth * Scale;
        long outputHeight = (long)ImagePixelHeight * Scale;
        DimensionsText = OutputSizeExceeded
            ? $"{ImagePixelWidth} × {ImagePixelHeight} → 超出上限"
            : $"{ImagePixelWidth} × {ImagePixelHeight} → {outputWidth} × {outputHeight}";
    }

    private void SetZoom(double value) => Zoom = Math.Clamp(value, 0.02, 8.0);

    private void ShowInfoBar(InfoBarSeverity severity, string title, string message, bool autoClose)
    {
        InfoBarSeverityValue = severity;
        InfoBarTitle = title;
        InfoBarMessage = message;

        if (!autoClose && severity != InfoBarSeverity.Success)
        {
            CanOpenOutputFolder = false;
        }

        IsInfoBarOpen = true;
        _infoBarTimer.Stop();
        if (autoClose)
        {
            _infoBarTimer.Start();
        }
    }

    private void CloseInfoBar()
    {
        _infoBarTimer.Stop();
        IsInfoBarOpen = false;
    }

    /// <summary>
    /// 进度节流：引擎（后台线程）每完成一个 tile 调用一次 Report；这里只在
    /// 阶段变化、进度变化 ≥1% 或距上次 ≥100ms 时才 Post 到 UI 线程，避免大图 100+ 块时 UI 线程被进度消息淹没。
    /// </summary>
    private sealed class ThrottledBatchProgress : IProgress<BatchProgress>
    {
        private const int MinIntervalMs = 100;
        private const double MinFractionDelta = 0.01;

        private readonly DispatcherQueue _dispatcher;
        private readonly Action<BatchProgress> _handler;
        private readonly Stopwatch _sinceLastPost = Stopwatch.StartNew();
        private BatchItemStage _lastStage;
        private double _lastFraction = -1;

        public ThrottledBatchProgress(DispatcherQueue dispatcher, Action<BatchProgress> handler)
        {
            _dispatcher = dispatcher;
            _handler = handler;
        }

        public void Report(BatchProgress value)
        {
            bool stageChanged = value.Stage != _lastStage;
            bool timeElapsed = _sinceLastPost.ElapsedMilliseconds >= MinIntervalMs;
            bool fractionChanged = value.OverallFraction - _lastFraction >= MinFractionDelta;
            if (!stageChanged && !timeElapsed && !fractionChanged)
            {
                return;
            }

            _lastStage = value.Stage;
            _lastFraction = value.OverallFraction;
            _sinceLastPost.Restart();
            _dispatcher.TryEnqueue(() => _handler(value));
        }
    }

    private static void Perf(string message)
    {
        if (PerfLogging)
        {
            AppLog.Info("[perf] " + message);
        }
    }

    private static string MemoryText()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSet = process.WorkingSet64 / (1024 * 1024);
            long peak = process.PeakWorkingSet64 / (1024 * 1024);
            long managed = GC.GetTotalMemory(false) / (1024 * 1024);
            return $"workingSet={workingSet}MB peak={peak}MB managed={managed}MB";
        }
        catch
        {
            return "memory=n/a";
        }
    }

    public void Dispose()
    {
        _infoBarTimer.Stop();
        try
        {
            _batchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        _batchCancellation?.Dispose();
        _batchCancellation = null;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        _thumbnailGate.Dispose();
    }
}
