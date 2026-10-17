using System.Runtime.InteropServices;

namespace Lumen.Core.Diagnostics;

/// <summary>
/// 轻量文件日志。GUI 应用没有控制台，排查启动/推理问题靠它。
/// 写入失败会被静默忽略，绝不因为日志影响主流程。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _file;
    private static bool _initialized;

    public static string LogFile
    {
        get
        {
            EnsureInitialized();
            return _file!;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Write(string level, string message, Exception? exception)
    {
        try
        {
            EnsureInitialized();
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (Gate)
            {
                File.AppendAllText(_file!, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                _file = Path.Combine(AppPaths.LogsDirectory, "lumen.log");
                if (File.Exists(_file) && new FileInfo(_file).Length > 4 * 1024 * 1024)
                {
                    File.Move(_file, _file + ".1", overwrite: true);
                }
            }
            catch
            {
                _file = Path.Combine(Path.GetTempPath(), "lumen.log");
            }

            _initialized = true;
        }
    }

    /// <summary>把未处理异常记录到日志，便于用户反馈时附上。</summary>
    public static void LogUnhandled(string source, Exception exception) =>
        Error($"未处理异常（{source}）：{exception.GetType().Name}", exception);

    public static string DescribeEnvironment()
    {
        try
        {
            return $"OS={RuntimeInformation.OSDescription}; Arch={RuntimeInformation.ProcessArchitecture}; Runtime={RuntimeInformation.FrameworkDescription}";
        }
        catch
        {
            return "环境信息不可用";
        }
    }
}
