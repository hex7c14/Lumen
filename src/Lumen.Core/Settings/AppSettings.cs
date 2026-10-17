using Lumen.Core.Batch;
using Lumen.Core.Imaging;

namespace Lumen.Core.Settings;

/// <summary>应用设置。持久化为 %LOCALAPPDATA%\Lumen\settings.json。</summary>
public sealed class AppSettings
{
    public string? SelectedModelId { get; set; }

    public int Scale { get; set; } = 4;

    /// <summary>处理模式：自动 / 强制 GPU / CPU 兼容。默认自动。</summary>
    public ComputeMode ComputeMode { get; set; } = ComputeMode.Auto;

    public bool PreferFp16 { get; set; } = true;

    /// <summary>0 表示自动。</summary>
    public int TileSize { get; set; }

    /// <summary>0 表示自动。</summary>
    public int TilePadding { get; set; }

    /// <summary>CPU 推理线程数；0 表示由 ONNX Runtime 自动决定。</summary>
    public int CpuThreads { get; set; }

    /// <summary>模型下拉框是否显示“通用/照片”模型（默认只显示动漫模型）。</summary>
    public bool ShowGeneralModels { get; set; }

    public OutputFormat OutputFormat { get; set; } = OutputFormat.Png;

    public int JpegQuality { get; set; } = 92;

    public int WebpQuality { get; set; } = 92;

    public bool PreserveAlpha { get; set; } = true;

    /// <summary>批量输出目录；null/空 = 保存到每张源图所在目录。</summary>
    public string? BatchOutputDirectory { get; set; }

    /// <summary>批量输出文件名模板（不含扩展名），token 见 BatchOptions。</summary>
    public string BatchFileNamePattern { get; set; } = "{name}_{scale}x";

    public OutputConflictPolicy BatchConflictPolicy { get; set; } = OutputConflictPolicy.AutoRename;

    public bool OpenOutputFolderWhenDone { get; set; }

    /// <summary>System / Light / Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>zh-CN / en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    public string? LastOpenDirectory { get; set; }

    public string? LastSaveDirectory { get; set; }

    public string? LastBatchInputDirectory { get; set; }

    public int WindowWidth { get; set; } = 1320;

    public int WindowHeight { get; set; } = 860;

    public bool ShowWelcomeTips { get; set; } = true;
}

/// <summary>设置存储。</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>JSON 文件实现，容忍损坏文件并自动重写。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        AppSettings? loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                        if (loaded is not null)
                        {
                            return loaded;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Diagnostics.AppLog.Error("读取设置失败，使用默认值。", ex);
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.AppLog.Error("保存设置失败。", ex);
            }
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase,
                allowIntegerValues: true),
        },
    };
}
