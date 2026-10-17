using Lumen.App.Services;
using Lumen.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Lumen.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>进程级服务集合，在首次创建窗口之前装配完成。</summary>
    public static AppServices Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info($"应用启动。{AppLog.DescribeEnvironment()}");
        Services = AppServices.CreateDefault();

        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// 走到这里的异常都没有被页面本地捕获（业务异常在 ViewModel/视图里已有 try/catch）。
    /// 只记录，不吞掉：让标准崩溃路径产生 WER dump，避免应用带着已损坏的状态继续运行。
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.LogUnhandled("Xaml", e.Exception);
        e.Handled = false;
    }
}
