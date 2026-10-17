using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Lumen.App.Services;

/// <summary>
/// 文件/文件夹选择器。WinRT 选择器在 unpackaged 应用里必须先用窗口 HWND 初始化，
/// 否则会抛 “The picker is not initialized” 之类的 COM 错误。
/// </summary>
public sealed class FileDialogService
{
    private static readonly string[] DecodeExtensions =
    [
        ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif",
    ];

    /// <summary>由 MainWindow 在 InitializeComponent 之后注入。</summary>
    public Window? Owner { get; set; }

    /// <summary>多选图片（批量添加）。</summary>
    public async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        if (Owner is null)
        {
            AppLog.Warn("文件选择器尚未绑定窗口，忽略添加请求。");
            return [];
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };

            foreach (string extension in DecodeExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            Initialize(picker);
            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            return files.Select(file => file.Path).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("打开图片选择器失败。", ex);
            return [];
        }
    }

    /// <summary>选择输出目录（批量输出设置）。</summary>
    public async Task<string?> PickFolderAsync()
    {
        if (Owner is null)
        {
            AppLog.Warn("文件夹选择器尚未绑定窗口，忽略请求。");
            return null;
        }

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };

            // FolderPicker 在桌面应用中同样需要至少一个 FileTypeFilter。
            picker.FileTypeFilter.Add("*");

            Initialize(picker);
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Error("打开文件夹选择器失败。", ex);
            return null;
        }
    }

    /// <summary>单张图片选择（保留给调试/兼容路径）。</summary>
    public async Task<string?> PickImageAsync()
    {
        IReadOnlyList<string> files = await PickImagesAsync();
        return files.Count > 0 ? files[0] : null;
    }

    /// <summary>保存对话框（迭代 1 单张导出路径保留；v2 默认批量自动保存）。</summary>
    public async Task<string?> PickSavePathAsync(string suggestedFileName, OutputFormat format, string? sourcePath)
    {
        if (Owner is null)
        {
            return null;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };

            (string label, string extension) = format switch
            {
                OutputFormat.Jpeg => ("JPEG 图片", ".jpg"),
                OutputFormat.WebP => ("WebP 图片", ".webp"),
                _ => ("PNG 图片", ".png"),
            };

            picker.FileTypeChoices.Add(label, new List<string> { extension });

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                try
                {
                    picker.SuggestedSaveFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"无法把保存对话框定位到源目录：{ex.Message}");
                }
            }

            picker.SuggestedFileName = suggestedFileName;
            Initialize(picker);
            StorageFile? file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Error("保存对话框失败。", ex);
            return null;
        }
    }

    private void Initialize(object picker)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Owner!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
