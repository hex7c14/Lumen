using System.ComponentModel;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Lumen.App.Views;

/// <summary>
/// 主编辑视图：工具栏 + 左侧批处理队列 + 画布 + 右侧参数面板 + 状态条。
/// 状态在 <see cref="MainViewModel"/>；这里只做纯视图工作（焦点、拖放、快捷键、系统信息对话框、UIA 名称同步）。
/// </summary>
public sealed partial class EditorView : UserControl
{
    private static readonly string[] SupportedDropExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif",
    ];

    private readonly KeyboardAccelerator _zoomInOemAccelerator;
    private readonly KeyboardAccelerator _zoomOutOemAccelerator;
    private readonly HashSet<QueueItem> _trackedQueueItems = [];
    private bool _loadedOnce;
    private bool _spaceHeld;

    public EditorView()
    {
        // App.OnLaunched 在创建窗口之前装配好服务，保证这里的 VM 在 XAML 载入阶段就可用。
        Services = App.Services;
        Vm = new MainViewModel(Services);

        InitializeComponent();

        // Ctrl+= / Ctrl+- 在主键盘上是 VK_OEM_PLUS / VK_OEM_MINUS（187 / 189），
        // VirtualKey 枚举没有对应名字，只能用数值创建加速器；小键盘 +/- 走 XAML 里的 Add/Subtract。
        _zoomInOemAccelerator = new KeyboardAccelerator
        {
            Key = (VirtualKey)0xBB,
            Modifiers = VirtualKeyModifiers.Control,
        };
        _zoomInOemAccelerator.Invoked += OnZoomInAccelerator;
        RootGrid.KeyboardAccelerators.Add(_zoomInOemAccelerator);

        _zoomOutOemAccelerator = new KeyboardAccelerator
        {
            Key = (VirtualKey)0xBD,
            Modifiers = VirtualKeyModifiers.Control,
        };
        _zoomOutOemAccelerator.Invoked += OnZoomOutAccelerator;
        RootGrid.KeyboardAccelerators.Add(_zoomOutOemAccelerator);

        Vm.FitRequested += OnFitRequested;
        Viewer.LostFocus += OnViewerLostFocus;
        QueueList.ContainerContentChanging += OnQueueContainerContentChanging;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AppServices Services { get; }

    public MainViewModel Vm { get; }

    /// <summary>让画布获得键盘焦点（Space / 方向键等由画布消费，而不是触发工具栏按钮）。</summary>
    public void FocusCanvas() => Viewer.Focus(FocusState.Programmatic);

    /// <summary>复位“按住 Space 看原图”等临时输入状态（窗口失活、画布失焦时调用）。</summary>
    public void ResetTransientInput()
    {
        if (!_spaceHeld)
        {
            return;
        }

        _spaceHeld = false;
        Viewer.ShowOriginalTemporarily(false);
    }

    // ───────────── 系统信息对话框 ─────────────

    /// <summary>标题栏“系统信息”入口：OS/CPU/GPU/DirectML/模型目录/日志/版本 + 复制与打开文件夹。</summary>
    public async Task ShowSystemInfoAsync()
    {
        try
        {
            var content = new StackPanel { Spacing = 10, MinWidth = 560 };
            content.Children.Add(CreateSystemInfoRow("版本", Vm.SystemInfoVersion, "TxtSysVersion"));
            content.Children.Add(CreateSystemInfoRow("系统", Vm.SystemInfoOs, "TxtSysOs"));
            content.Children.Add(CreateSystemInfoRow("运行时", Vm.SystemInfoRuntime, "TxtSysRuntime"));
            content.Children.Add(CreateSystemInfoRow("CPU", Vm.SystemInfoCpu, "TxtSysCpu"));
            content.Children.Add(CreateSystemInfoRow("GPU", Vm.SystemInfoGpu, "TxtSysGpu"));
            content.Children.Add(CreateSystemInfoRow("DirectML", Vm.SystemInfoDirectMl, "TxtSysDirectMl"));
            content.Children.Add(CreateSystemInfoRow("模型目录", Vm.SystemInfoModels, "TxtSysModels"));
            content.Children.Add(CreateSystemInfoRow("批处理服务", Vm.SystemInfoBatchService, "TxtSysBatch"));
            content.Children.Add(CreateSystemInfoRow("日志文件", Vm.SystemInfoLogPath, "TxtSysLogPath"));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var copyButton = new Button { Content = "复制信息" };
            AutomationProperties.SetAutomationId(copyButton, "BtnCopySystemInfo");
            AutomationProperties.SetName(copyButton, "复制系统信息");
            copyButton.Click += (_, _) => CopySystemInfoToClipboard();
            buttons.Children.Add(copyButton);

            var logsButton = new Button { Content = "打开日志文件夹" };
            AutomationProperties.SetAutomationId(logsButton, "BtnOpenLogFolder");
            AutomationProperties.SetName(logsButton, "打开日志文件夹");
            logsButton.Click += (_, _) => Vm.OpenLogFolderCommand.Execute(null);
            buttons.Children.Add(logsButton);

            var modelsButton = new Button { Content = "打开模型目录" };
            AutomationProperties.SetAutomationId(modelsButton, "BtnOpenModelsFolder");
            AutomationProperties.SetName(modelsButton, "打开模型目录");
            modelsButton.Click += (_, _) => Vm.OpenModelsFolderCommand.Execute(null);
            buttons.Children.Add(modelsButton);

            var outputButton = new Button { Content = "打开输出文件夹" };
            AutomationProperties.SetAutomationId(outputButton, "BtnOpenOutputFolderFromInfo");
            AutomationProperties.SetName(outputButton, "打开输出文件夹");
            outputButton.Click += (_, _) => Vm.OpenOutputFolderCommand.Execute(null);
            buttons.Children.Add(outputButton);

            content.Children.Add(buttons);
            AutomationProperties.SetAutomationId(content, "SystemInfoPanel");

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme,
                Title = "系统信息",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            AutomationProperties.SetAutomationId(dialog, "DlgSystemInfo");

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("显示系统信息失败。", ex);
        }
    }

    private static Grid CreateSystemInfoRow(string label, string value, string automationId)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(valueBlock, automationId);
        Grid.SetColumn(valueBlock, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        return row;
    }
    private void CopySystemInfoToClipboard()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(Vm.BuildSystemInfoText());
            Clipboard.SetContent(package);
            Vm.StatusText = "系统信息已复制";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"复制系统信息失败：{ex.Message}");
        }
    }

    // ───────────── 生命周期 ─────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        FocusCanvas();

        try
        {
            await RunStartupAutomationAsync();
        }
        catch (Exception ex)
        {
            // LUMEN_* 调试自动化只用于截图/验证，失败不应让应用崩溃。
            AppLog.Error("启动自动化失败。", ex);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Vm.Dispose();

    private void OnViewerLostFocus(object sender, RoutedEventArgs e) => ResetTransientInput();

    /// <summary>
    /// 无人值守截图/联调支持（仅调试用环境变量，UI 不暴露）：
    /// LUMEN_OPEN 单张 / LUMEN_OPEN_MANY（; 分隔）→ 启动后加入队列；
    /// LUMEN_SCALE=2|3|4、LUMEN_BACKEND=auto|gpu|cpu、LUMEN_CONFLICT、LUMEN_PATTERN、LUMEN_SAVE_DIR、LUMEN_SAVE_TO
    /// 由 ViewModel 在构造时套用；LUMEN_AUTOSTART=1 启动后自动开始全部；LUMEN_FORCE_NO_DML=1 强制无 DirectML；
    /// LUMEN_SIMULATE_BATCH=1 批处理服务不可用时用模拟进度（仅截图验证）。
    /// LUMEN_THEME=Dark|Light 由 MainWindow 处理。
    /// </summary>
    private async Task RunStartupAutomationAsync()
    {
        string? scaleText = Environment.GetEnvironmentVariable("LUMEN_SCALE");
        if (int.TryParse(scaleText, out int scale) && scale is >= 2 and <= 4)
        {
            Vm.ScaleIndex = scale - 2;
        }

        var startupPaths = new List<string>();
        string? single = Environment.GetEnvironmentVariable("LUMEN_OPEN");
        if (!string.IsNullOrWhiteSpace(single))
        {
            startupPaths.Add(single);
        }

        string? many = Environment.GetEnvironmentVariable("LUMEN_OPEN_MANY");
        if (!string.IsNullOrWhiteSpace(many))
        {
            foreach (string part in many.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                startupPaths.Add(part);
            }
        }

        if (startupPaths.Count > 0)
        {
            await Vm.AddPathsAsync(startupPaths);
            FocusCanvas();
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("LUMEN_AUTOSTART"), "1", StringComparison.Ordinal))
        {
            return;
        }

        // 给首帧渲染留一点时间，截图时才能看到“处理中”的进度覆盖层。
        await Task.Delay(300);
        await Vm.StartBatchAsync(null);
    }

    private void OnFitRequested(object? sender, EventArgs e) => Viewer.FitToWindow();

    // ───────────── 队列 UIA 名称同步 ─────────────

    private void OnQueueContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is null || args.Item is not QueueItem item)
        {
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, item.AccessibleName);
        if (_trackedQueueItems.Add(item))
        {
            item.PropertyChanged += OnQueueItemPropertyChanged;
        }
    }

    private void OnQueueItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not QueueItem item)
        {
            return;
        }

        if (e.PropertyName is not (nameof(QueueItem.AccessibleName) or nameof(QueueItem.StatusLine) or nameof(QueueItem.IsFailed)))
        {
            return;
        }

        if (QueueList.ContainerFromItem(item) is ListViewItem container)
        {
            AutomationProperties.SetName(container, item.AccessibleName);
        }
    }

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem item)
        {
            Vm.SelectedQueueItem = item;
        }
    }

    // ───────────── 快捷键（Ctrl+O / Ctrl+0 / Ctrl+= / Ctrl+- / Esc / Space） ─────────────

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Vm.CanAdd)
        {
            _ = Vm.AddImagesCommand.ExecuteAsync(null);
        }
    }

    private void OnFitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Vm.CanZoom)
        {
            Vm.FitCommand.Execute(null);
        }
    }

    private void OnZoomInAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Vm.CanZoom)
        {
            Vm.ZoomInCommand.Execute(null);
        }
    }

    private void OnZoomOutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Vm.CanZoom)
        {
            Vm.ZoomOutCommand.Execute(null);
        }
    }

    /// <summary>
    /// Esc：取消批处理 → 退出对比 → 关闭 InfoBar。三者都不适用时不标记 Handled，
    /// 让 ComboBox/Expander 等控件自己处理 Esc。
    /// </summary>
    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Vm.IsProcessing)
        {
            args.Handled = true;
            Vm.CancelCommand.Execute(null);
        }
        else if (Vm.IsCompareMode)
        {
            args.Handled = true;
            Vm.IsCompareMode = false;
        }
        else if (Vm.IsInfoBarOpen)
        {
            args.Handled = true;
            Vm.DismissInfoBarCommand.Execute(null);
        }
    }

    /// <summary>
    /// Space 是“按住临时看原图”。只在画布自身拥有键盘焦点时拦截，
    /// 其余焦点（按钮/开关等）保留系统默认的 Space 激活行为。
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !Vm.HasImage)
        {
            return;
        }

        object? focused = FocusManager.GetFocusedElement(XamlRoot);
        if (!ReferenceEquals(focused, Viewer))
        {
            return;
        }

        if (!_spaceHeld)
        {
            _spaceHeld = true;
            Viewer.ShowOriginalTemporarily(true);
        }

        e.Handled = true;
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || !_spaceHeld)
        {
            return;
        }

        _spaceHeld = false;
        Viewer.ShowOriginalTemporarily(false);
        e.Handled = true;
    }

    // ───────────── 拖放（多文件 + 文件夹） ─────────────

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.Caption = "添加到队列";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }

        DropHighlight.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnRootDragLeave(object sender, DragEventArgs e) =>
        DropHighlight.Visibility = Visibility.Collapsed;

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (IStorageItem storageItem in items)
            {
                if (storageItem is StorageFolder folder)
                {
                    paths.Add(folder.Path);
                }
                else if (storageItem is StorageFile file && SupportedDropExtensions.Contains(file.FileType.ToLowerInvariant()))
                {
                    paths.Add(file.Path);
                }
            }

            if (paths.Count > 0)
            {
                await Vm.AddPathsAsync(paths);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            AppLog.Error("处理拖放文件失败。", ex);
        }
    }

    // ───────────── 控件事件 ─────────────

    private void OnCompareToggleChecked(object sender, RoutedEventArgs e) => Vm.IsCompareMode = true;

    private void OnCompareToggleUnchecked(object sender, RoutedEventArgs e) => Vm.IsCompareMode = false;
}
