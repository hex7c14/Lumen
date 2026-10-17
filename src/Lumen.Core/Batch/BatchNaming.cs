using Lumen.Core.Imaging;

namespace Lumen.Core.Batch;

/// <summary>
/// 批量输出命名。模板支持 token（见 <see cref="BatchOptions.FileNamePattern"/>）：
/// <c>{name}</c> 原文件名（不含扩展名）、<c>{ext}</c> 源扩展名（不含点）、<c>{scale}</c> 目标倍率、
/// <c>{model}</c> 模型 id、<c>{index}</c> 序号（1 基）、<c>{date}</c> 日期 yyyyMMdd。
/// 模板产生的名字不含扩展名，扩展名由 <see cref="SaveOptions.Format"/> 决定。
/// </summary>
public static class BatchNaming
{
    /// <summary>按模板展开出不含扩展名的文件名。</summary>
    public static string Expand(
        string pattern,
        string inputPath,
        int index,
        int scale,
        string modelId,
        DateTime timestampUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (index < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "序号必须从 1 开始。");
        }

        string name = Path.GetFileNameWithoutExtension(inputPath);
        string extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        DateTime localTimestamp = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc.ToLocalTime() : timestampUtc;

        string result = pattern
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", extension, StringComparison.OrdinalIgnoreCase)
            .Replace("{scale}", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{model}", modelId, StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", localTimestamp.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        result = SanitizeFileName(result);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("文件名模板展开后为空，请检查模板内容。");
        }

        return result;
    }

    /// <summary>输出扩展名（含点），由保存格式决定。</summary>
    public static string ExtensionFor(OutputFormat format) => format switch
    {
        OutputFormat.Png => ".png",
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.WebP => ".webp",
        OutputFormat.Bmp => ".bmp",
        _ => ".png",
    };

    /// <summary>
    /// 计算目标输出路径（不做冲突处理）。<paramref name="outputDirectory"/> 为空时使用源文件目录。
    /// </summary>
    public static string BuildOutputPath(
        string? outputDirectory,
        string inputPath,
        string fileNameWithoutExtension,
        OutputFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameWithoutExtension);

        string directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Path.GetTempPath()
            : Path.GetFullPath(outputDirectory);
        return Path.Combine(directory, fileNameWithoutExtension + ExtensionFor(format));
    }

    /// <summary>
    /// 按冲突策略解析最终路径。返回 null 表示 <see cref="OutputConflictPolicy.Skip"/> 且文件已存在，
    /// 调用方应把该项标记为 <see cref="BatchItemStatus.Skipped"/>。
    /// </summary>
    public static string? ResolveConflict(string desiredPath, OutputConflictPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        return policy switch
        {
            OutputConflictPolicy.Overwrite => desiredPath,
            OutputConflictPolicy.Skip => null,
            _ => FindAvailableName(desiredPath),
        };
    }

    /// <summary>在名字后追加 “ (2)”、“ (3)”…，直到找到不存在的路径。</summary>
    public static string FindAvailableName(string desiredPath)
    {
        string directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = Path.Combine(directory, $"{fileName} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为“{Path.GetFileName(desiredPath)}”找到不冲突的输出文件名。");
    }

    /// <summary>去掉文件名中的非法字符，避免模板引入目录穿越或系统不接受的名字。</summary>
    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];
        int length = 0;
        foreach (char c in value)
        {
            if (Array.IndexOf(invalid, c) >= 0)
            {
                continue;
            }

            buffer[length++] = c;
        }

        return new string(buffer, 0, length).Trim();
    }
}
