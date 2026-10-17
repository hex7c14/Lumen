using System.Diagnostics;
using Lumen.Core;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Cli.Commands;

/// <summary>upscale：真实图片超分并保存。</summary>
internal static class UpscaleCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options = CliOptions.Parse(args);
        string? inputPath = options.Get("--input");
        string? outputPath = options.Get("--output");
        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("用法：Lumen.Cli upscale --input <图片> --output <输出> --model <id> [--scale 4] [--backend auto|gpu|cpu] [--fp16 on|off] [--tile N] [--pad N] [--save-format png|jpeg|webp] [--uniform-pad on|off] [--cpu-compat on|off] [--threads N]");
            return 2;
        }

        ModelCatalog catalog = new(AppPaths.ModelsRoot);
        ModelDescriptor? model = ResolveModel(catalog, options.Get("--model"));
        if (model is null)
        {
            return 1;
        }

        int targetScale = options.GetInt("--scale", model.Scale);
        if (targetScale < 1 || targetScale > model.Scale)
        {
            Console.Error.WriteLine($"--scale 必须在 1..{model.Scale} 之间（模型固有倍率 {model.Scale}）。");
            return 2;
        }

        OutputFormat format = CliSupport.ResolveOutputFormat(options.Get("--save-format"), outputPath);
        var codec = new SkiaImageCodec();
        LoadedImage image;
        try
        {
            image = codec.Load(inputPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"读取输入图片失败：{ex.Message}");
            return 1;
        }

        bool uniformTileShape = options.GetBool("--uniform-pad", true);
        var upscaleOptions = new UpscaleOptions
        {
            Model = model,
            TargetScale = targetScale,
            Backend = options.GetBackend(),
            PreferFp16 = options.GetBool("--fp16", true),
            TileSize = options.GetInt("--tile", 0),
            TilePadding = options.GetInt("--pad", 0),
            PreviewMaxDimension = options.GetInt("--preview", 2560),
            MaxOutputPixels = options.GetInt("--max-output-pixels", 400_000_000),
            CpuThreads = options.GetInt("--threads", 0),
            CpuCompatibilityMode = options.GetBool("--cpu-compat", false),
        };

        Console.WriteLine($"模型    : {model.Id}（{model.DisplayName}，x{model.Scale}，{(model.HasFp16 ? "fp32+fp16" : "fp32")}）");
        Console.WriteLine($"输入    : {Path.GetFullPath(inputPath)}  {image.Width}x{image.Height}  透明={image.HasTransparency}");
        Console.WriteLine($"目标    : x{targetScale} → {image.Width * targetScale}x{image.Height * targetScale}，后端={CliSupport.FormatBackendName(upscaleOptions.Backend)}，uniformPad={uniformTileShape}");
        Console.WriteLine();

        using var service = new OnnxUpscalerService(codec) { PadTilesToUniformShape = uniformTileShape };
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("收到 Ctrl+C，正在取消…");
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var reporter = new ConsoleProgressReporter();
        try
        {
            var watch = Stopwatch.StartNew();
            UpscaleResult result = await service.UpscaleAsync(image, upscaleOptions, reporter, cts.Token);
            watch.Stop();
            reporter.FinishLine();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            codec.Save(result.Output, outputPath, new SaveOptions(format, options.GetInt("--quality", 92)));

            var fileInfo = new FileInfo(outputPath);
            double tileAverage = result.TileCount == 0
                ? 0
                : service.LastTileTimings.Sum(t => t.Elapsed.TotalMilliseconds) / result.TileCount;

            Console.WriteLine();
            Console.WriteLine($"输出    : {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"尺寸    : {result.OutputWidth}x{result.OutputHeight}（{format}，{fileInfo.Length / 1024.0 / 1024.0:F2} MB）");
            Console.WriteLine($"后端    : {result.Backend}   精度: {result.Precision}");
            Console.WriteLine($"分块    : tile={result.TileSize} 块数={result.TileCount} 平均单块={tileAverage:F1} ms  uniformPad={uniformTileShape}");
            Console.WriteLine($"耗时    : 总 {result.Duration.TotalMilliseconds:F0} ms" +
                              $"（会话创建/命中 {service.LastSessionCreationTime.TotalMilliseconds:F0} ms）");
            if (result.Preview is not null)
            {
                Console.WriteLine($"预览    : {result.Preview.Width}x{result.Preview.Height}（长边 ≤ {upscaleOptions.PreviewMaxDimension}）");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            reporter.FinishLine();
            Console.Error.WriteLine("已取消，未写出输出文件。");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static ModelDescriptor? ResolveModel(ModelCatalog catalog, string? requestedId)
    {
        if (catalog.Models.Count == 0)
        {
            Console.Error.WriteLine($"模型目录为空：{catalog.ModelsRoot}。请先运行 tools/model-pipeline 获取模型。");
            return null;
        }

        ModelDescriptor? model = string.IsNullOrWhiteSpace(requestedId) ? catalog.Default : catalog.Find(requestedId);
        if (model is null)
        {
            Console.Error.WriteLine($"未找到模型“{requestedId}”。可用模型：");
            foreach (ModelDescriptor item in catalog.Models)
            {
                Console.Error.WriteLine($"  {item.Id}（{item.DisplayName}，x{item.Scale}）");
            }
        }

        return model;
    }
}
