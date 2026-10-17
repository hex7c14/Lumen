using System.Runtime.InteropServices;
using Lumen.App.Views;
using Lumen.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Color = Windows.UI.Color;

namespace Lumen.App;

public sealed partial class MainWindow : Window
{
    private const int MinWidthDip = 1040;
    private const int MinHeightDip = 680;
    private const int DefaultWidthDip = 1320;
    private const int DefaultHeightDip = 860;

    private bool _forceClose;
    private bool _closeDialogOpen;
    private bool _isForcedTheme;
    private bool _resizingToMinimum;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "Lumen";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyRequestedTheme();
        TryApplyMicaBackdrop();
        SetWindowIcon();
        ApplyDefaultSize();

        App.Services.FileDialogs.Owner = this;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnWindowActivated;
        RootGrid.ActualThemeChanged += OnRootGridActualThemeChanged;

        AppLog.Info("主窗口已创建。");
    }

    // ───────────── 主题 ─────────────

    /// <summary>
    /// 调试环境变量 LUMEN_THEME=Dark|Light：强制根元素主题，用于无人值守深浅色截图。
    /// 其余情况跟随系统（不设置 RequestedTheme）。
    /// </summary>
    private void ApplyRequestedTheme()
    {
        string? theme = Environment.GetEnvironmentVariable("LUMEN_THEME");
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            _isForcedTheme = true;
            RootGrid.RequestedTheme = ElementTheme.Light;
        }
        else if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            _isForcedTheme = true;
            RootGrid.RequestedTheme = ElementTheme.Dark;
        }

        if (_isForcedTheme)
        {
            ApplyCaptionButtonColors(RootGrid.RequestedTheme == ElementTheme.Light);
        }
    }

    private void OnRootGridActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_isForcedTheme)
        {
            ApplyCaptionButtonColors(sender.ActualTheme == ElementTheme.Light);
        }
    }

    private void ApplyCaptionButtonColors(bool isLight)
    {
        try
        {
            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            if (isLight)
            {
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0x5C, 0x5C, 0x5C);
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0, 0, 0);
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(0x99, 0, 0, 0);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x24, 0, 0, 0);
            }
            else
            {
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0xA0, 0xA0, 0xA0);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置标题栏按钮颜色失败：{ex.Message}");
        }
    }

    private void TryApplyMicaBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Mica 不可用，回退到亚克力：{ex.Message}");
        }

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"亚克力也不可用，使用主题纯色背景：{ex.Message}");
            FallbackBackground.Visibility = Visibility.Visible;
        }
    }

    // ───────────── 窗口图标 / 尺寸 ─────────────

    private void SetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗口图标失败：{ex.Message}");
        }
    }

    private void ApplyDefaultSize()
    {
        try
        {
            int desiredWidthDip = Math.Max(MinWidthDip, App.Services.Settings.WindowWidth);
            int desiredHeightDip = Math.Max(MinHeightDip, App.Services.Settings.WindowHeight);
            double scale = GetDpiScale();
            int width = (int)Math.Round(desiredWidthDip * scale);
            int height = (int)Math.Round(desiredHeightDip * scale);

            DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = display.WorkArea;

            // 工作区比最小尺寸还小时，以工作区为准，避免拉起后又被最小尺寸逻辑撑出屏幕。
            int minimumWidth = (int)Math.Round(MinWidthDip * scale);
            int minimumHeight = (int)Math.Round(MinHeightDip * scale);
            width = work.Width >= minimumWidth
                ? Math.Clamp(width, minimumWidth, work.Width)
                : work.Width;
            height = work.Height >= minimumHeight
                ? Math.Clamp(height, minimumHeight, work.Height)
                : work.Height;

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗口尺寸失败：{ex.Message}");
        }
    }

    /// <summary>
    /// WinUI 3 的 OverlappedPresenter 没有最小尺寸属性，这里在窗口尺寸变化后把被压到下限以下的维度拉回，
    /// 效果等同于 “最小 1040 × 680”。
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _resizingToMinimum)
        {
            return;
        }

        try
        {
            SizeInt32 size = AppWindow.Size;
            double scale = GetDpiScale();
            int minimumWidth = (int)Math.Round(MinWidthDip * scale);
            int minimumHeight = (int)Math.Round(MinHeightDip * scale);

            if (size.Width >= minimumWidth && size.Height >= minimumHeight)
            {
                return;
            }

            // 屏幕工作区比最小尺寸还小时，允许窗口跟随屏幕，而不是强行撑大。
            DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = display.WorkArea;
            int targetWidth = work.Width >= minimumWidth ? Math.Max(size.Width, minimumWidth) : work.Width;
            int targetHeight = work.Height >= minimumHeight ? Math.Max(size.Height, minimumHeight) : work.Height;

            _resizingToMinimum = true;
            AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"约束最小窗口尺寸失败：{ex.Message}");
        }
        finally
        {
            _resizingToMinimum = false;
        }
    }

    private double GetDpiScale()
    {
        try
        {
            IntPtr hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    // ───────────── 窗口激活状态 ─────────────

    /// <summary>Alt+Tab / 点击任务栏离开窗口时复位“按住 Space 看原图”等临时输入状态。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Editor.ResetTransientInput();
        }
    }

    // ───────────── 关闭确认 ─────────────

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            if (_forceClose)
            {
                PersistSettings();
                return;
            }

            if (!Editor.Vm.IsProcessing)
            {
                PersistSettings();
                return;
            }

            // 正在处理：先拦下关闭，确认后再关。
            args.Cancel = true;

            if (_closeDialogOpen)
            {
                // 对话框已经打开，重复的关闭请求直接忽略（避免第二个 ContentDialog 抛异常）。
                return;
            }

            _closeDialogOpen = true;
            ContentDialogResult result;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    // 强制主题（LUMEN_THEME）下让对话框跟随窗口根元素主题，而不是系统主题。
                    RequestedTheme = RootGrid.RequestedTheme,
                    Title = "正在处理",
                    Content = "正在超分，退出将丢失当前结果。",
                    PrimaryButtonText = "取消处理并退出",
                    CloseButtonText = "继续处理",
                    DefaultButton = ContentDialogButton.Close,
                };

                result = await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"关闭确认对话框显示失败：{ex.Message}");
                return;
            }
            finally
            {
                _closeDialogOpen = false;
            }

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            Editor.Vm.CancelProcessing();

            // 给引擎一点收敛时间（分块之间会检查取消），最多等 3 秒，避免带着后台推理退出。
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (Editor.Vm.IsProcessing && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            _forceClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("关闭窗口流程失败。", ex);
            _closeDialogOpen = false;
            _forceClose = true;
            Close();
        }
    }

    // ───────────── 系统信息 ─────────────

    private async void OnSystemInfoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Editor.ShowSystemInfoAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("打开系统信息失败。", ex);
        }
    }

    private void PersistSettings()
    {
        try
        {
            double scale = GetDpiScale();
            SizeInt32 size = AppWindow.Size;
            Editor.Vm.PersistSettings(
                (int)Math.Round(size.Width / scale),
                (int)Math.Round(size.Height / scale));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存窗口设置失败：{ex.Message}");
        }
    }
}
