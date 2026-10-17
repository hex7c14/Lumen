using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumen.App.Helpers;
using Lumen.Core.Batch;
using Lumen.Core.Imaging;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.ViewModels;

/// <summary>
/// 批处理队列中的一张图片。持有展示状态（缩略图/状态行/进度/结果路径），
/// 右键命令委托给 <see cref="MainViewModel"/> 执行。
/// </summary>
public sealed partial class QueueItem : ObservableObject
{
    private readonly MainViewModel _owner;

    public QueueItem(MainViewModel owner, string path)
    {
        _owner = owner;
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        DirectoryPath = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        StatusLine = "等待";
    }

    /// <summary>绝对路径（去重键）。</summary>
    public string Path { get; }

    public string FileName { get; }

    public string DirectoryPath { get; }

    /// <summary>48×48 缩略图（UI 线程创建的 WriteableBitmap）。</summary>
    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    /// <summary>状态行：等待 / 处理中 34% / 完成 · 12.4s / 失败。</summary>
    [ObservableProperty]
    public partial string StatusLine { get; set; }

    /// <summary>当前项进度 0..100（仅处理中可见）。</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>是否有成功输出（决定“打开输出文件/在文件夹中显示”可用）。</summary>
    [ObservableProperty]
    public partial bool HasOutput { get; set; }

    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    public partial int InputWidth { get; set; }

    [ObservableProperty]
    public partial int InputHeight { get; set; }

    [ObservableProperty]
    public partial int OutputWidth { get; set; }

    [ObservableProperty]
    public partial int OutputHeight { get; set; }

    [ObservableProperty]
    public partial string? Backend { get; set; }

    /// <summary>结果预览像素（批量服务返回；内存策略：最多保留最近 8 张，其余从磁盘懒加载）。</summary>
    public ImagePixels? ResultPreview { get; set; }

    /// <summary>供 ListViewItem 的 UIA Name 使用：“文件名 · 状态”。</summary>
    public string AccessibleName => $"{FileName} · {StatusLine}";

    partial void OnStatusLineChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    // ───────────── 右键菜单命令 ─────────────

    [RelayCommand]
    private void Reprocess() => _owner.ReprocessItem(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveItem(this);

    [RelayCommand]
    private void OpenOutput() => _owner.OpenFile(this.OutputPath);

    [RelayCommand]
    private void RevealOutput() => _owner.RevealFileInFolder(this.OutputPath);

    // ───────────── 状态机 ─────────────

    public bool IsPending => !IsProcessing && !HasOutput && !IsFailed;

    public void MarkPending()
    {
        IsFailed = false;
        IsProcessing = false;
        HasOutput = false;
        OutputPath = null;
        ErrorMessage = null;
        Progress = 0;
        StatusLine = "等待";
    }

    public void MarkProcessing(double itemFraction)
    {
        IsProcessing = true;
        IsFailed = false;
        Progress = Math.Clamp(itemFraction * 100.0, 0, 100);
        StatusLine = $"处理中 {Progress:0}%";
    }

    public void MarkLoading()
    {
        IsProcessing = true;
        Progress = 0;
        StatusLine = "读取中";
    }

    public void MarkSaving()
    {
        IsProcessing = true;
        Progress = 100;
        StatusLine = "保存中";
    }

    public void MarkCompleted(BatchItemResult result)
    {
        IsProcessing = false;
        IsFailed = false;
        HasOutput = true;
        Progress = 100;
        OutputPath = result.OutputPath;
        ErrorMessage = null;
        InputWidth = result.InputWidth;
        InputHeight = result.InputHeight;
        OutputWidth = result.OutputWidth;
        OutputHeight = result.OutputHeight;
        Backend = result.Backend;
        ResultPreview = result.Preview;
        StatusLine = $"完成 · {Ui.Elapsed(result.Duration)}";
    }

    public void MarkSkipped(string? plannedOutputPath)
    {
        IsProcessing = false;
        IsFailed = false;
        HasOutput = false;
        Progress = 0;
        OutputPath = plannedOutputPath;
        StatusLine = "已跳过";
    }

    public void MarkCanceled()
    {
        IsProcessing = false;
        IsFailed = false;
        Progress = 0;
        StatusLine = "已取消";
    }

    public void MarkFailed(string message)
    {
        IsProcessing = false;
        IsFailed = true;
        HasOutput = false;
        Progress = 0;
        ErrorMessage = message;
        StatusLine = "失败";
    }
}
