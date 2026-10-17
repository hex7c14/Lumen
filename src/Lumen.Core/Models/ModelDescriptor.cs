using System.Text.Json.Serialization;

namespace Lumen.Core.Models;

/// <summary>模型定位：速度优先还是质量优先。UI 用它分组展示。</summary>
public enum ModelQualityTier
{
    Fast = 0,
    Balanced = 1,
    Quality = 2,
}

/// <summary>模型适用题材。</summary>
public enum ModelCategory
{
    Photo = 0,
    Anime = 1,
    General = 2,
}

/// <summary>模型权重的数值精度。</summary>
public enum ModelPrecision
{
    Fp32 = 0,
    Fp16 = 1,
}

/// <summary>models/&lt;id&gt;/model.json 的映射。字段名与 model.json 契约保持一致。</summary>
public sealed class ModelDescriptor
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("scale")]
    public required int Scale { get; init; }

    [JsonPropertyName("arch")]
    public string Arch { get; init; } = string.Empty;

    [JsonPropertyName("qualityTier")]
    public ModelQualityTier QualityTier { get; init; } = ModelQualityTier.Quality;

    [JsonPropertyName("recommendedFor")]
    public IReadOnlyList<ModelCategory> RecommendedFor { get; init; } = [];

    [JsonPropertyName("inputLayout")]
    public string InputLayout { get; init; } = "NCHW";

    [JsonPropertyName("outputLayout")]
    public string OutputLayout { get; init; } = "NCHW";

    [JsonPropertyName("dynamicAxes")]
    public bool DynamicAxes { get; init; } = true;

    [JsonPropertyName("files")]
    public ModelFileSet Files { get; init; } = new();

    [JsonPropertyName("sha256")]
    public IReadOnlyDictionary<string, string> Sha256 { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; init; } = string.Empty;

    [JsonPropertyName("license")]
    public string License { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    /// <summary>是否为应用默认模型（建议只在一个模型上置 true）。</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>模型所在目录（运行时填充，不来自 JSON）。</summary>
    [JsonIgnore]
    public string DirectoryPath { get; internal set; } = string.Empty;

    [JsonIgnore]
    public string? Fp16RelativePath => Files.Fp16;

    [JsonIgnore]
    public bool HasFp16 => !string.IsNullOrWhiteSpace(Files.Fp16);

    [JsonIgnore]
    public bool IsRecommendedForPhoto => RecommendedFor.Contains(ModelCategory.Photo);

    public bool Supports(ModelPrecision precision) => precision == ModelPrecision.Fp32 || HasFp16;

    /// <summary>解析权重文件绝对路径；<paramref name="precision"/> 不可用时回退 fp32。</summary>
    public string ResolveModelPath(ModelPrecision precision)
    {
        string? relative = precision == ModelPrecision.Fp16 && HasFp16 ? Files.Fp16 : Files.Fp32;
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new InvalidOperationException($"模型 {Id} 缺少 {(precision == ModelPrecision.Fp16 ? "fp16" : "fp32")} 权重文件定义。");
        }

        return Path.GetFullPath(Path.Combine(DirectoryPath, relative));
    }

    public ModelPrecision ResolvePrecision(bool preferFp16) =>
        preferFp16 && HasFp16 ? ModelPrecision.Fp16 : ModelPrecision.Fp32;

    public override string ToString() => $"{DisplayName} ({Id}, x{Scale})";
}

/// <summary>权重文件集合。</summary>
public sealed class ModelFileSet
{
    [JsonPropertyName("fp32")]
    public string? Fp32 { get; init; } = "model.onnx";

    [JsonPropertyName("fp16")]
    public string? Fp16 { get; init; } = "model.fp16.onnx";
}
