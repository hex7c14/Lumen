using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Tests;

public sealed class UpscaleOptionsTests
{
    private static ModelDescriptor CreateModel() => new()
    {
        Id = "unit-test",
        DisplayName = "单元测试",
        Scale = 4,
        Files = new ModelFileSet { Fp32 = "model.onnx", Fp16 = "model.fp16.onnx" },
    };

    [Fact]
    public void Validate_AcceptsValidOptions()
    {
        var options = new UpscaleOptions
        {
            Model = CreateModel(),
            TargetScale = 2,
            TileSize = 256,
            TilePadding = 24,
        };

        options.Validate();
    }

    [Fact]
    public void Validate_NullModel_Throws()
    {
        var options = new UpscaleOptions { Model = null! };
        Assert.Throws<ArgumentNullException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Validate_TargetScaleOutOfRange_Throws(int targetScale)
    {
        var options = new UpscaleOptions { Model = CreateModel(), TargetScale = targetScale };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Validate_TileSizeOutOfRange_Throws(int tileSize)
    {
        var options = new UpscaleOptions { Model = CreateModel(), TileSize = tileSize };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1025)]
    public void Validate_TilePaddingOutOfRange_Throws(int padding)
    {
        var options = new UpscaleOptions { Model = CreateModel(), TilePadding = padding };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ModelWithZeroScale_Throws()
    {
        var model = new ModelDescriptor
        {
            Id = "broken",
            DisplayName = "坏模型",
            Scale = 0,
            Files = new ModelFileSet { Fp32 = "model.onnx" },
        };

        var options = new UpscaleOptions { Model = model };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Defaults_MatchContract()
    {
        var options = new UpscaleOptions { Model = CreateModel() };
        Assert.Equal(4, options.TargetScale);
        Assert.Equal(ExecutionBackend.Auto, options.Backend);
        Assert.True(options.PreferFp16);
        Assert.True(options.PreserveAlpha);
        Assert.Equal(0, options.TileSize);
        Assert.Equal(0, options.TilePadding);
        Assert.Equal(2560, options.PreviewMaxDimension);
        Assert.Equal(400_000_000, options.MaxOutputPixels);
        Assert.Equal(0, options.GpuDeviceId);
    }
}
