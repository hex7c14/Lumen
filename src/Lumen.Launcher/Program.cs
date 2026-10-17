using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lumen.Launcher;

/// <summary>
/// 发布版根目录启动器：根目录只暴露 Lumen.exe / models / output / README，
/// 真正的 WinUI 程序与全部运行时依赖放在 runtime\ 子目录（发布脚本会设为隐藏）。
/// 启动器把命令行参数原样转给 runtime\Lumen.exe，并把工作目录设为安装根目录。
/// </summary>
internal static class Program
{
    private const string RuntimeFolderName = "runtime";
    private const string RuntimeExecutableName = "Lumen.exe";
    private const uint MB_ICONERROR = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        try
        {
            string root = AppContext.BaseDirectory;
            string runtimeExecutable = Path.Combine(root, RuntimeFolderName, RuntimeExecutableName);
            if (!File.Exists(runtimeExecutable))
            {
                ShowError(
                    "找不到程序运行文件：\r\n" + runtimeExecutable + "\r\n\r\n" +
                    "请确认解压时保留了完整文件夹（Lumen.exe、models、output、runtime），" +
                    "不要只复制根目录的 Lumen.exe。");
                return 1;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = runtimeExecutable,
                WorkingDirectory = root,
                UseShellExecute = false,
            };
            foreach (string argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception exception)
        {
            ShowError("启动失败：" + exception.Message);
            return 1;
        }
    }

    private static void ShowError(string message)
    {
        _ = MessageBoxW(IntPtr.Zero, message, "Lumen", MB_ICONERROR);
    }
}
