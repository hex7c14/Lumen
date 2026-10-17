using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumen.Core.Models;

/// <summary>模型目录。扫描 models/ 下每个子目录中的 model.json。</summary>
public interface IModelCatalog
{
    string ModelsRoot { get; }

    IReadOnlyList<ModelDescriptor> Models { get; }

    ModelDescriptor? Find(string? id);

    /// <summary>默认模型：优先推荐给照片、质量档、且有 fp16 权重的模型。</summary>
    ModelDescriptor? Default { get; }

    void Reload();
}

public sealed class ModelCatalog : IModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private List<ModelDescriptor> _models = [];

    public ModelCatalog(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ModelsRoot = modelsRoot;
        Reload();
    }

    public string ModelsRoot { get; }

    public IReadOnlyList<ModelDescriptor> Models => _models;

    public ModelDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public ModelDescriptor? Default
    {
        get
        {
            if (_models.Count == 0)
            {
                return null;
            }

            // 1) model.json 显式标记的默认模型；2) 推荐给照片的质量档模型；3) 质量档最高者。
            return _models.FirstOrDefault(m => m.IsDefault)
                   ?? _models
                       .Where(m => m.RecommendedFor.Contains(ModelCategory.Photo))
                       .OrderByDescending(m => m.QualityTier)
                       .ThenByDescending(m => m.HasFp16)
                       .FirstOrDefault()
                   ?? _models.OrderByDescending(m => m.QualityTier).First();
        }
    }

    public void Reload()
    {
        var models = new List<ModelDescriptor>();
        if (Directory.Exists(ModelsRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(ModelsRoot))
            {
                string jsonPath = Path.Combine(directory, "model.json");
                if (!File.Exists(jsonPath))
                {
                    continue;
                }

                try
                {
                    ModelDescriptor? descriptor = JsonSerializer.Deserialize<ModelDescriptor>(File.ReadAllText(jsonPath), JsonOptions);
                    if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Id))
                    {
                        continue;
                    }

                    descriptor.DirectoryPath = Path.GetFullPath(directory);
                    if (!File.Exists(descriptor.ResolveModelPath(ModelPrecision.Fp32)))
                    {
                        // 权重文件缺失的模型不进入目录，避免用户选中后才报错。
                        continue;
                    }

                    models.Add(descriptor);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
                {
                    System.Diagnostics.Debug.WriteLine($"跳过无效模型 {jsonPath}: {ex.Message}");
                }
            }
        }

        _models = models
            .OrderByDescending(m => m.QualityTier)
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}
