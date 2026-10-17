using Lumen.Core.Batch;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Core.Tests;

public sealed class BatchNamingTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 17, 2, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Expand_ReplacesAllTokens()
    {
        string name = BatchNaming.Expand(
            "{name}_{model}_{scale}x_{index}_{date}",
            @"C:\photos\My Photo 01.JPG",
            index: 3,
            scale: 4,
            modelId: "anime-model",
            timestampUtc: FixedUtc);

        Assert.Equal("My Photo 01_anime-model_4x_3_20261017", name);
    }

    [Fact]
    public void Expand_SupportsExtToken()
    {
        Assert.Equal("a_jpg", BatchNaming.Expand("{name}_{ext}", @"C:\x\a.JPG", 1, 4, "m", FixedUtc));
    }

    [Fact]
    public void Expand_RemovesInvalidFileNameCharacters()
    {
        string name = BatchNaming.Expand("{name}", @"C:\x\a<b>c|d.png", 1, 4, "m", FixedUtc);
        Assert.DoesNotContain('<', name);
        Assert.DoesNotContain('>', name);
        Assert.DoesNotContain('|', name);
        Assert.Equal("abcd", name);
    }

    [Fact]
    public void Expand_EmptyAfterSanitize_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BatchNaming.Expand("< >", @"C:\x\a.png", 1, 4, "m", FixedUtc));
    }

    [Fact]
    public void Expand_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => BatchNaming.Expand(" ", @"C:\x\a.png", 1, 4, "m", FixedUtc));
        Assert.Throws<ArgumentException>(() => BatchNaming.Expand("{name}", " ", 1, 4, "m", FixedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => BatchNaming.Expand("{name}", @"C:\x\a.png", 0, 4, "m", FixedUtc));
    }

    [Theory]
    [InlineData(OutputFormat.Png, ".png")]
    [InlineData(OutputFormat.Jpeg, ".jpg")]
    [InlineData(OutputFormat.WebP, ".webp")]
    [InlineData(OutputFormat.Bmp, ".bmp")]
    public void ExtensionFor_FollowsSaveFormat(OutputFormat format, string expected)
    {
        Assert.Equal(expected, BatchNaming.ExtensionFor(format));
    }

    [Fact]
    public void BuildOutputPath_UsesSourceDirectoryWhenOutputDirectoryEmpty()
    {
        string path = BatchNaming.BuildOutputPath(null, @"C:\photos\a.png", "a_4x", OutputFormat.Png);
        Assert.Equal(Path.GetFullPath(@"C:\photos\a_4x.png"), path);
    }

    [Fact]
    public void BuildOutputPath_UsesOutputDirectory()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), "lumen-out");
        string path = BatchNaming.BuildOutputPath(outputDirectory, @"C:\photos\a.png", "a_4x", OutputFormat.Jpeg);
        Assert.Equal(Path.Combine(outputDirectory, "a_4x.jpg"), path);
    }

    [Fact]
    public void ResolveConflict_AutoRenameAppendsNumber()
    {
        using var temp = new TempDirectory();
        string desired = Path.Combine(temp.Path, "a_4x.png");
        File.WriteAllText(desired, "existing");

        string first = BatchNaming.ResolveConflict(desired, OutputConflictPolicy.AutoRename)!;
        Assert.Equal(Path.Combine(temp.Path, "a_4x (2).png"), first);

        File.WriteAllText(first, "existing2");
        string second = BatchNaming.ResolveConflict(desired, OutputConflictPolicy.AutoRename)!;
        Assert.Equal(Path.Combine(temp.Path, "a_4x (3).png"), second);

        string free = Path.Combine(temp.Path, "free.png");
        Assert.Equal(free, BatchNaming.ResolveConflict(free, OutputConflictPolicy.AutoRename));
    }

    [Fact]
    public void ResolveConflict_OverwriteKeepsPath()
    {
        using var temp = new TempDirectory();
        string desired = Path.Combine(temp.Path, "a.png");
        File.WriteAllText(desired, "existing");

        Assert.Equal(desired, BatchNaming.ResolveConflict(desired, OutputConflictPolicy.Overwrite));
    }

    [Fact]
    public void ResolveConflict_SkipReturnsNullWhenExists()
    {
        using var temp = new TempDirectory();
        string desired = Path.Combine(temp.Path, "a.png");
        File.WriteAllText(desired, "existing");

        Assert.Null(BatchNaming.ResolveConflict(desired, OutputConflictPolicy.Skip));
        string free = Path.Combine(temp.Path, "b.png");
        Assert.Equal(free, BatchNaming.ResolveConflict(free, OutputConflictPolicy.Skip));
    }
}

public sealed class BatchUpscalerServiceTests
{
    private static ModelDescriptor CreateModel() => new()
    {
        Id = "anime-test",
        DisplayName = "测试动漫模型",
        Scale = 4,
        Arch = "rrdb",
        Files = new ModelFileSet { Fp32 = "model.onnx", Fp16 = "model.fp16.onnx" },
    };

    private static BatchOptions CreateOptions(
        string? outputDirectory,
        OutputConflictPolicy policy = OutputConflictPolicy.AutoRename,
        bool keepPreview = true)
    {
        return new BatchOptions
        {
            Upscale = new UpscaleOptions
            {
                Model = CreateModel(),
                TargetScale = 4,
                Backend = ExecutionBackend.Cpu,
                PreferFp16 = false,
                PreviewMaxDimension = 2560,
            },
            Save = SaveOptions.Default,
            OutputDirectory = outputDirectory,
            FileNamePattern = "{name}_{scale}x",
            ConflictPolicy = policy,
            KeepPreview = keepPreview,
            PreviewMaxDimension = 512,
        };
    }

    [Fact]
    public async Task ProcessAsync_CompletesAllItems_AndWritesOutputs()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.jpg")];
        BatchOptions options = CreateOptions(temp.Combine("out"));

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, options);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(BatchItemStatus.Completed, r.Status);
            Assert.NotNull(r.OutputPath);
            Assert.Equal(32, r.OutputWidth);
            Assert.Equal(24, r.OutputHeight);
            Assert.NotNull(r.Preview);
            Assert.Equal("CPU", r.Backend);
        });
        Assert.Equal(2, codec.Saved.Count);
        Assert.Contains(results, r => r.OutputPath!.EndsWith("a_4x.png", StringComparison.Ordinal));
        Assert.Contains(results, r => r.OutputPath!.EndsWith("b_4x.png", StringComparison.Ordinal));
        Assert.Equal(2, Directory.GetFiles(temp.Combine("out")).Length);
    }

    [Fact]
    public async Task ProcessAsync_CorruptFile_FailsOnlyThatItem_AndContinues()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        string corrupt = temp.Combine("broken.png");
        codec.FailLoad.Add(corrupt);
        string[] inputs = [temp.Combine("a.png"), corrupt, temp.Combine("b.png")];

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")));

        Assert.Equal(BatchItemStatus.Completed, results[0].Status);
        Assert.Equal(BatchItemStatus.Failed, results[1].Status);
        Assert.Null(results[1].OutputPath); // 失败项不暴露输出路径
        Assert.False(string.IsNullOrWhiteSpace(results[1].ErrorMessage));
        Assert.Contains("解码", results[1].ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(BatchItemStatus.Completed, results[2].Status);
        Assert.Equal(2, codec.Saved.Count);
    }

    [Fact]
    public async Task ProcessAsync_AutoRename_AvoidsSameNameConflicts()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("in1"));
        Directory.CreateDirectory(temp.Combine("in2"));
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        string[] inputs = [temp.Combine("in1/same.png"), temp.Combine("in2/same.png")];

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")));

        Assert.All(results, r => Assert.Equal(BatchItemStatus.Completed, r.Status));
        Assert.NotEqual(results[0].OutputPath, results[1].OutputPath);
        Assert.Contains(" (2)", results[1].OutputPath!, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(temp.Combine("out")).Length);
    }

    [Fact]
    public async Task ProcessAsync_SkipPolicy_SkipsExistingOutputWithoutProcessing()
    {
        using var temp = new TempDirectory();
        string outputDirectory = temp.Combine("out");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(temp.Combine("in"));
        File.WriteAllText(Path.Combine(outputDirectory, "same_4x.png"), "existing");

        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        string[] inputs = [temp.Combine("in/same.png")];

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(
            inputs,
            CreateOptions(outputDirectory, OutputConflictPolicy.Skip));

        BatchItemResult item = Assert.Single(results);
        Assert.Equal(BatchItemStatus.Skipped, item.Status);
        Assert.Equal(Path.Combine(outputDirectory, "same_4x.png"), item.OutputPath);
        Assert.Equal(0, upscaler.CallCount);
        Assert.Empty(codec.Saved);
    }

    [Fact]
    public async Task ProcessAsync_PreCanceled_ReturnsAllCanceled_WithoutThrowing()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.png")];

        IReadOnlyList<BatchItemResult> results =
            await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")), cancellationToken: cts.Token);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(BatchItemStatus.Canceled, r.Status);
            Assert.Null(r.OutputPath); // 取消项不暴露输出路径
        });
        Assert.Equal(0, upscaler.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_CanceledMidBatch_MarksCurrentAndRemainingCanceled()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        using var cts = new CancellationTokenSource();
        upscaler.OnFirstCall = () => cts.Cancel();
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.png"), temp.Combine("c.png")];

        IReadOnlyList<BatchItemResult> results =
            await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")), cancellationToken: cts.Token);

        Assert.Equal(BatchItemStatus.Canceled, results[0].Status);
        Assert.Equal(BatchItemStatus.Canceled, results[1].Status);
        Assert.Equal(BatchItemStatus.Canceled, results[2].Status);
        Assert.Empty(codec.Saved);
    }

    [Fact]
    public async Task ProcessAsync_ReportsProgressStagesAndFractions()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        var reports = new List<BatchProgress>();
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.png")];

        await service.ProcessAsync(
            inputs,
            CreateOptions(temp.Combine("out")),
            new InlineTestProgress<BatchProgress>(reports.Add));

        Assert.Contains(reports, r => r.Stage == BatchItemStage.Loading);
        Assert.Contains(reports, r => r.Stage == BatchItemStage.Upscaling);
        Assert.Contains(reports, r => r.Stage == BatchItemStage.Saving);
        Assert.Contains(reports, r => r.Stage == BatchItemStage.Completed);
        Assert.All(reports, r =>
        {
            Assert.InRange(r.OverallFraction, 0.0, 1.0);
            Assert.InRange(r.ItemFraction, 0.0, 1.0);
            Assert.InRange(r.ItemIndex, 0, 1);
            Assert.Equal(2, r.ItemCount);
        });

        // 第二项完成时整体进度应为 1.0。
        BatchProgress lastCompleted = reports.Last(r => r.Stage == BatchItemStage.Completed && r.ItemIndex == 1);
        Assert.Equal(1.0, lastCompleted.OverallFraction, 6);
        // 块数透传。
        Assert.Contains(reports, r => r.TilesTotal > 0 && r.TilesCompleted > 0);
        // Upscaling 阶段映射到 5%–95%。
        Assert.Contains(reports, r => r.Stage == BatchItemStage.Upscaling && r.ItemFraction is >= 0.05 and <= 0.95);
    }

    [Fact]
    public async Task ProcessAsync_KeepPreviewFalse_DoesNotKeepPreview()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        BatchOptions options = CreateOptions(temp.Combine("out"), keepPreview: false);
        string[] inputs = [temp.Combine("a.png")];

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, options);

        Assert.Null(Assert.Single(results).Preview);
        Assert.Equal(0, upscaler.LastOptions!.PreviewMaxDimension);
    }

    [Fact]
    public async Task ProcessAsync_PreviewDimension_IsMinOfUpscaleAndBatch()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        BatchOptions options = CreateOptions(temp.Combine("out"));
        string[] inputs = [temp.Combine("a.png")];

        await service.ProcessAsync(inputs, options);

        Assert.Equal(512, upscaler.LastOptions!.PreviewMaxDimension);
    }

    [Fact]
    public async Task ProcessAsync_EmptyInput_ReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var service = new BatchUpscalerService(new FakeUpscaler(), new FakeCodec());
        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync([], CreateOptions(temp.Combine("out")));
        Assert.Empty(results);
    }

    [Fact]
    public async Task ProcessAsync_InvalidOptions_Throws()
    {
        var service = new BatchUpscalerService(new FakeUpscaler(), new FakeCodec());
        BatchOptions options = CreateOptions(null);
        options = new BatchOptions
        {
            Upscale = options.Upscale,
            Save = SaveOptions.Default,
            FileNamePattern = " ",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessAsync([@"C:\x\a.png"], options));
    }

    [Fact]
    public async Task ProcessAsync_SaveFailure_MarksOnlyThatItemFailed_AndContinues()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec();
        codec.FailSaveFileNames.Add("a_4x.png");
        var upscaler = new FakeUpscaler();
        var service = new BatchUpscalerService(upscaler, codec);
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.png")];

        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")));

        Assert.Equal(BatchItemStatus.Failed, results[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(results[0].ErrorMessage));
        Assert.Equal(BatchItemStatus.Completed, results[1].Status);
        Assert.NotNull(results[1].OutputPath);
        Assert.Single(codec.Saved);
    }

    [Fact]
    public async Task ProcessAsync_OverlapsSaveWithNextInference()
    {
        using var temp = new TempDirectory();
        var codec = new FakeCodec { SaveDelay = TimeSpan.FromMilliseconds(80) };
        var upscaler = new FakeUpscaler { Delay = TimeSpan.FromMilliseconds(80) };
        var service = new BatchUpscalerService(upscaler, codec);
        string[] inputs = [temp.Combine("a.png"), temp.Combine("b.png"), temp.Combine("c.png")];

        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<BatchItemResult> results = await service.ProcessAsync(inputs, CreateOptions(temp.Combine("out")));
        watch.Stop();

        Assert.All(results, r => Assert.Equal(BatchItemStatus.Completed, r.Status));
        Assert.Equal(3, codec.Saved.Count);
        // 串行实现约为 3*(推理 80 + 保存 80) = 480ms；流水线应把保存藏到下一张推理后面。
        Assert.True(
            watch.ElapsedMilliseconds < 430,
            $"流水线未生效：3 张耗时 {watch.ElapsedMilliseconds}ms（串行约 480ms）。");
    }

    private sealed class InlineTestProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineTestProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    private sealed class FakeCodec : IImageCodec
    {
        public HashSet<string> FailLoad { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> FailSaveFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TimeSpan SaveDelay { get; init; }

        public List<(string Path, ImagePixels Image)> Saved { get; } = [];

        public IReadOnlyCollection<string> SupportedDecodeExtensions { get; } = [".png", ".jpg", ".jpeg"];

        public LoadedImage Load(string path)
        {
            if (FailLoad.Contains(path))
            {
                throw new InvalidDataException("伪造的损坏文件");
            }

            return new LoadedImage(TestImages.CreateGradient(8, 6));
        }

        public LoadedImage Load(Stream stream, string? hintName = null) => throw new NotSupportedException();

        public ImagePixels Resize(ImagePixels source, int targetWidth, int targetHeight, ResampleMode mode = ResampleMode.HighQuality)
            => TestImages.CreateGradient(targetWidth, targetHeight);

        public ImagePixels CreatePreview(ImagePixels source, int maxWidth, int maxHeight)
            => TestImages.CreateGradient(Math.Min(source.Width, maxWidth), Math.Min(source.Height, maxHeight));

        public void Save(ImagePixels image, string path, SaveOptions options)
        {
            if (SaveDelay > TimeSpan.Zero)
            {
                Thread.Sleep(SaveDelay);
            }

            if (FailSaveFileNames.Contains(Path.GetFileName(path)))
            {
                throw new IOException("伪造的保存失败");
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, "saved");
            Saved.Add((path, image));
        }
    }

    private sealed class FakeUpscaler : IUpscalerService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Action? OnFirstCall { get; set; }

        public TimeSpan Delay { get; init; }

        public UpscaleOptions? LastOptions { get; private set; }

        public async Task<UpscaleResult> UpscaleAsync(
            LoadedImage input,
            UpscaleOptions options,
            IProgress<UpscaleProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                OnFirstCall?.Invoke();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new UpscaleProgress
            {
                Fraction = 0.5,
                Stage = UpscaleStage.Inference,
                TilesCompleted = 2,
                TilesTotal = 4,
            });
            progress?.Report(new UpscaleProgress
            {
                Fraction = 1.0,
                Stage = UpscaleStage.Completed,
                TilesCompleted = 4,
                TilesTotal = 4,
            });

            var output = TestImages.CreateGradient(input.Width * options.TargetScale, input.Height * options.TargetScale);
            return new UpscaleResult
            {
                Output = output,
                Preview = TestImages.CreateGradient(16, 12),
                Duration = TimeSpan.FromMilliseconds(5),
                Backend = "CPU",
                ModelId = options.Model.Id,
                Precision = ModelPrecision.Fp32,
                TileSize = 256,
                TileCount = 4,
            };
        }

        public void ClearSessionCache()
        {
        }

        public void Dispose()
        {
        }
    }
}

public sealed class BatchOptionsTests
{
    private static BatchOptions CreateOptions(string? outputDirectory) => new()
    {
        Upscale = new UpscaleOptions
        {
            Model = new ModelDescriptor
            {
                Id = "anime-test",
                DisplayName = "测试动漫模型",
                Scale = 4,
                Files = new ModelFileSet { Fp32 = "model.onnx" },
            },
        },
        Save = SaveOptions.Default,
        OutputDirectory = outputDirectory,
    };

    [Fact]
    public void Validate_AcceptsNullOrEmptyOutputDirectory()
    {
        CreateOptions(null).Validate();
        CreateOptions("  ").Validate();
    }

    [Fact]
    public void Validate_AcceptsNormalOutputDirectory()
    {
        using var temp = new TempDirectory();
        CreateOptions(temp.Combine("out")).Validate();
    }

    [Fact]
    public void Validate_RejectsInvalidPathCharacters()
    {
        BatchOptions options = CreateOptions("bad\0dir");
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_RejectsDriveRoot()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())) ?? "C:\\";
        BatchOptions options = CreateOptions(root);
        ArgumentException exception = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("根目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InvalidPattern_Throws()
    {
        BatchOptions options = CreateOptions(null);
        var invalid = new BatchOptions
        {
            Upscale = options.Upscale,
            Save = options.Save,
            FileNamePattern = "bad/name",
        };
        Assert.Throws<ArgumentException>(() => invalid.Validate());
    }
}
