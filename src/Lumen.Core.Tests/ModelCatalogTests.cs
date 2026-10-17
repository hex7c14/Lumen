using Lumen.Core.Models;

namespace Lumen.Core.Tests;

public sealed class ModelCatalogTests
{
    private const string ValidModelJson = """
        {
          "id": "test-model",
          "displayName": "测试模型",
          "description": "单元测试用",
          "scale": 4,
          "arch": "rrdb",
          "qualityTier": "quality",
          "recommendedFor": ["photo"],
          "inputLayout": "NCHW",
          "outputLayout": "NCHW",
          "dynamicAxes": true,
          "files": { "fp32": "model.onnx", "fp16": "model.fp16.onnx" },
          "sizeBytes": 123456,
          "sourceUrl": "https://example.com/model",
          "license": "BSD-3-Clause",
          "author": "test"
        }
        """;

    [Fact]
    public void LoadsValidModelDescriptor()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("models/test-model/model.json", ValidModelJson);
        temp.CreateFile("models/test-model/model.onnx", string.Empty);
        temp.CreateFile("models/test-model/model.fp16.onnx", string.Empty);

        var catalog = new ModelCatalog(temp.Combine("models"));

        ModelDescriptor model = Assert.Single(catalog.Models);
        Assert.Equal("test-model", model.Id);
        Assert.Equal("测试模型", model.DisplayName);
        Assert.Equal(4, model.Scale);
        Assert.Equal(ModelQualityTier.Quality, model.QualityTier);
        Assert.Equal(new[] { ModelCategory.Photo }, model.RecommendedFor);
        Assert.True(model.DynamicAxes);
        Assert.True(model.HasFp16);
        Assert.Equal(ModelPrecision.Fp16, model.ResolvePrecision(preferFp16: true));
        Assert.Equal(ModelPrecision.Fp32, model.ResolvePrecision(preferFp16: false));
        Assert.Equal(
            Path.GetFullPath(temp.Combine("models/test-model/model.fp16.onnx")),
            model.ResolveModelPath(ModelPrecision.Fp16));
        Assert.Equal(
            Path.GetFullPath(temp.Combine("models/test-model/model.onnx")),
            model.ResolveModelPath(ModelPrecision.Fp32));
        Assert.Same(model, catalog.Find("TEST-MODEL"));
        Assert.Null(catalog.Find("missing"));
        Assert.Null(catalog.Find(null));
        Assert.Same(model, catalog.Default);
    }

    [Fact]
    public void SkipsModelWithoutFp32Weights()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("models/test-model/model.json", ValidModelJson);

        var catalog = new ModelCatalog(temp.Combine("models"));

        Assert.Empty(catalog.Models);
        Assert.Null(catalog.Default);
    }

    [Fact]
    public void SkipsInvalidJson()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("models/broken/model.json", "{ not json");
        temp.CreateFile("models/broken/model.onnx", string.Empty);
        temp.CreateFile("dir-without-json/readme.txt", "x");

        var catalog = new ModelCatalog(temp.Combine("models"));

        Assert.Empty(catalog.Models);
    }

    [Fact]
    public void SkipsDescriptorWithoutId()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(
            "models/broken/model.json",
            """{ "displayName": "无 id", "scale": 4, "files": { "fp32": "model.onnx" } }""");
        temp.CreateFile("models/broken/model.onnx", string.Empty);

        var catalog = new ModelCatalog(temp.Combine("models"));

        Assert.Empty(catalog.Models);
    }

    [Fact]
    public void DefaultPrefersPhotoQualityModel()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(
            "models/fast-model/model.json",
            ValidModelJson
                .Replace("\"test-model\"", "\"fast-model\"", StringComparison.Ordinal)
                .Replace("\"qualityTier\": \"quality\"", "\"qualityTier\": \"fast\"", StringComparison.Ordinal)
                .Replace("[\"photo\"]", "[\"general\"]", StringComparison.Ordinal));
        temp.CreateFile("models/fast-model/model.onnx", string.Empty);
        temp.CreateFile(
            "models/quality-model/model.json",
            ValidModelJson.Replace("\"test-model\"", "\"quality-model\"", StringComparison.Ordinal));
        temp.CreateFile("models/quality-model/model.onnx", string.Empty);

        var catalog = new ModelCatalog(temp.Combine("models"));

        Assert.Equal(2, catalog.Models.Count);
        Assert.Equal("quality-model", catalog.Default?.Id);
    }

    [Fact]
    public void Reload_PicksUpNewModels()
    {
        using var temp = new TempDirectory();
        var catalog = new ModelCatalog(temp.Combine("models"));
        Assert.Empty(catalog.Models);

        temp.CreateFile("models/test-model/model.json", ValidModelJson);
        temp.CreateFile("models/test-model/model.onnx", string.Empty);
        catalog.Reload();

        Assert.Single(catalog.Models);
    }
}
