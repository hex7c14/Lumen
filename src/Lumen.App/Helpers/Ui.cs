using System.Reflection;
using Lumen.Core.Imaging;
using Microsoft.UI.Xaml;

namespace Lumen.App.Helpers;

/// <summary>
/// x:Bind 静态函数集合。XAML 里用 <c>{x:Bind helpers:Ui.Visible(Vm.HasImage), Mode=OneWay}</c>，
/// 避免为每个布尔量写 IValueConverter（见 WinUI 设计技能：优先静态函数）。
/// </summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>值为 false 时可见（避免在 XAML 里嵌套函数调用）。</summary>
    public static Visibility VisibleWhenFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>输出预估的“正常”态：未超限、也未到警告阈值。</summary>
    public static Visibility OutputSizeNormal(bool exceeded, bool warning) =>
        !exceeded && !warning ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>展开时 300 DIP，折叠时只留 48 DIP 的图标栏。</summary>
    public static double PanelWidth(bool expanded) => expanded ? 300 : 48;

    /// <summary>左侧队列面板：展开 280 DIP，折叠只留 48 DIP。</summary>
    public static double QueuePanelWidth(bool collapsed) => collapsed ? 48 : 280;

    /// <summary>应用版本（来自程序集信息，供系统信息对话框显示）。</summary>
    public static string AppVersion()
    {
        try
        {
            Assembly assembly = typeof(Ui).Assembly;
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    /// <summary>文件大小，例如 “64 MB”“4.6 MB”。</summary>
    public static string FileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
    }

    /// <summary>耗时，例如 “12.4s”“1m 05s”。</summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds >= 60)
        {
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";
        }

        return $"{elapsed.TotalSeconds:0.0}s";
    }

    /// <summary>分块进度里的时钟样式，例如 “00:12”。</summary>
    public static string Clock(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>滑块数字，例如 “92”。</summary>
    public static string Number(double value) => $"{value:0}";

    /// <summary>把异常翻译成用户可读的中文，不把堆栈抛给界面。</summary>
    public static string UserMessage(Exception exception) => exception switch
    {
        OutOfMemoryException => "内存不足：图片或输出尺寸过大，请降低倍率后重试。",
        OperationCanceledException => "操作已取消。",
        InvalidDataException => "图片数据无法解析，文件可能已损坏。",
        UnauthorizedAccessException => "没有访问该文件的权限，请检查路径或换一个目录。",
        IOException => "读写文件时出错，请检查磁盘空间与文件占用。",
        InvalidOperationException or ArgumentException => exception.Message,
        _ => "发生未预期的错误，详细信息已写入日志。",
    };

    /// <summary>输出格式的默认扩展名。</summary>
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.WebP => ".webp",
        OutputFormat.Bmp => ".bmp",
        _ => ".png",
    };

    /// <summary>当前缩放倍数是否处于“适应窗口”以外的状态（仅用于提示文案，不参与布局）。</summary>
    public static string Zoom(double zoom) => $"{zoom * 100:0}%";
}
