using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Batch;

/// <summary>输出文件命名冲突策略。</summary>
public enum OutputConflictPolicy
{
    /// <summary>自动追加序号（默认）：name_4x.png → name_4x (2).png。</summary>
    AutoRename = 0,

    /// <summary>直接覆盖已存在文件。</summary>
    Overwrite = 1,

    /// <summary>跳过不处理该张。</summary>
    Skip = 2,
}

/// <summary>批处理中单张图片的阶段。</summary>
public enum BatchItemStage
{
    Pending = 0,
    Loading = 1,
    Upscaling = 2,
    Saving = 3,
    Completed = 4,
    Failed = 5,
    Skipped = 6,
    Canceled = 7,
}

/// <summary>批处理中单张图片的最终状态。</summary>
public enum BatchItemStatus
{
    Completed = 0,
    Failed = 1,
    Skipped = 2,
    Canceled = 3,
}

/// <summary>
/// 批量超分选项。输出目录为空表示保存到每张源图所在目录。
/// 文件名模板支持 token：{name} 原文件名（不含扩展名）、{ext} 源扩展名、{scale} 倍率、
/// {model} 模型 id、{index} 序号（从 1 开始）、{date} 日期 yyyyMMdd。
/// </summary>
public sealed class BatchOptions
{
    public required UpscaleOptions Upscale { get; init; }

    public required SaveOptions Save { get; init; }

    /// <summary>输出目录；null/空 = 源文件所在目录。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>文件名模板（不含扩展名）。</summary>
    public string FileNamePattern { get; init; } = "{name}_{scale}x";

    public OutputConflictPolicy ConflictPolicy { get; init; } = OutputConflictPolicy.AutoRename;

    /// <summary>是否为每张结果保留预览图（UI 对比用；大批量时可关闭以省内存）。</summary>
    public bool KeepPreview { get; init; } = true;

    /// <summary>结果预览长边上限。</summary>
    public int PreviewMaxDimension { get; init; } = 1600;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Upscale);
        ArgumentNullException.ThrowIfNull(Save);
        Upscale.Validate();

        if (string.IsNullOrWhiteSpace(FileNamePattern))
        {
            throw new ArgumentException("文件名模板不能为空。", nameof(FileNamePattern));
        }

        if (FileNamePattern.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("文件名模板包含非法字符。", nameof(FileNamePattern));
        }

        if (PreviewMaxDimension is < 0 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(PreviewMaxDimension), PreviewMaxDimension, "预览长边必须在 0 到 8192 之间。");
        }

        ValidateOutputDirectory();
    }

    /// <summary>校验输出目录字符串：非法字符、无法解析的路径、磁盘根目录都给出中文异常（不要求目录已存在）。</summary>
    private void ValidateOutputDirectory()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return;
        }

        if (OutputDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("输出目录包含非法路径字符。", nameof(OutputDirectory));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(OutputDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"输出目录路径无效：{OutputDirectory}", nameof(OutputDirectory));
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (root.Length > 0 &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("输出目录不能是磁盘根目录，请选择一个子目录。", nameof(OutputDirectory));
        }
    }
}
