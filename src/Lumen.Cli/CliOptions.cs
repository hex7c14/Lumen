using Lumen.Core.Imaging;
using Lumen.Core.Upscaling;

namespace Lumen.Cli;

/// <summary>极简命令行参数解析：只认 <c>--name value</c> 形式（值允许以 -- 开头的负数除外）。</summary>
internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args, int startIndex = 1)
    {
        var options = new CliOptions();
        for (int i = startIndex; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数“{token}”（参数必须以 -- 开头）。");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 {token} 缺少值。");
            }

            options._values[token] = args[++i];
        }

        return options;
    }

    public string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    public bool Has(string name) => _values.ContainsKey(name);

    public int GetInt(string name, int fallback) =>
        _values.TryGetValue(name, out string? value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    public bool GetBool(string name, bool fallback)
    {
        if (!_values.TryGetValue(name, out string? value))
        {
            return fallback;
        }

        return value.ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => throw new ArgumentException($"参数 {name} 的值“{value}”无效，应为 on/off。"),
        };
    }

    public ExecutionBackend GetBackend(ExecutionBackend fallback = ExecutionBackend.Auto)
    {
        if (!_values.TryGetValue("--backend", out string? value))
        {
            return fallback;
        }

        return value.ToLowerInvariant() switch
        {
            "auto" => ExecutionBackend.Auto,
            "gpu" or "dml" or "directml" => ExecutionBackend.DirectML,
            "cpu" => ExecutionBackend.Cpu,
            _ => throw new ArgumentException($"参数 --backend 的值“{value}”无效，应为 auto/gpu/cpu。"),
        };
    }
}

internal static class CliSupport
{
    public static OutputFormat ResolveOutputFormat(string? explicitFormat, string outputPath)
    {
        string? format = explicitFormat;
        if (string.IsNullOrWhiteSpace(format))
        {
            format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        }

        return format.ToLowerInvariant() switch
        {
            "png" => OutputFormat.Png,
            "jpg" or "jpeg" or "jfif" => OutputFormat.Jpeg,
            "webp" => OutputFormat.WebP,
            "bmp" => OutputFormat.Bmp,
            _ => throw new ArgumentException($"无法从输出路径“{outputPath}”推断格式，请用 --save-format png|jpeg|webp|bmp 指定。"),
        };
    }

    public static string FormatBackendName(ExecutionBackend backend) => backend switch
    {
        ExecutionBackend.Auto => "auto",
        ExecutionBackend.DirectML => "gpu(DirectML)",
        ExecutionBackend.Cpu => "cpu",
        _ => backend.ToString(),
    };
}
