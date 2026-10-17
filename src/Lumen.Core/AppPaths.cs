namespace Lumen.Core;

/// <summary>
/// 应用路径约定。优先使用环境变量覆盖，便于开发与测试：
/// <c>LUMEN_DATA_DIR</c>、<c>LUMEN_MODELS_ROOT</c>。
/// </summary>
public static class AppPaths
{
    private const string ModelsFolderName = "models";

    public static string DataRoot { get; } = ResolveDataRoot();

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    public static string TempDirectory => Path.Combine(DataRoot, "temp");

    public static string ModelsRoot { get; } = ResolveModelsRoot();

    /// <summary>应用目录旁若存在 models/（发布布局）就优先使用。</summary>
    public static string InstallModelsRoot => Path.Combine(AppContext.BaseDirectory, ModelsFolderName);

    private static string ResolveDataRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable("LUMEN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Lumen");
    }

    private static string ResolveModelsRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable("LUMEN_MODELS_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        string installModels = InstallModelsRoot;
        if (ContainsModels(installModels))
        {
            return installModels;
        }

        // 开发布局：从 bin 目录向上找仓库根的 models/。
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, ModelsFolderName);
            if (ContainsModels(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(DataRoot, ModelsFolderName);
    }

    private static bool ContainsModels(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                if (File.Exists(Path.Combine(sub, "model.json")))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
