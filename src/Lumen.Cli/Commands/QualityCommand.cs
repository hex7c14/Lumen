using System.Diagnostics;
using Lumen.Core;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Cli.Commands;

/// <summary>
/// quality：把参考图用 Lanczos3 降采样 model.Scale 倍得到低分辨率输入，再分别用
/// ①神经网络模型 ②Lanczos3 放大回参考尺寸，计算 PSNR / SSIM 并与基线对比。
/// 参考图先裁剪到模型倍率的整数倍，保证输出尺寸严格等于参考图。
/// </summary>
internal static class QualityCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options = CliOptions.Parse(args);
        string? referencePath = options.Get("--reference");
        if (string.IsNullOrWhiteSpace(referencePath))
        {
            Console.Error.WriteLine("用法：Lumen.Cli quality --model <id> --reference <图片> [--backend auto|gpu|cpu] [--fp16 on|off]");
            return 2;
        }

        ModelCatalog catalog = new(AppPaths.ModelsRoot);
        ModelDescriptor? model = UpscaleCommand.ResolveModel(catalog, options.Get("--model"));
        if (model is null)
        {
            return 1;
        }

        var codec = new SkiaImageCodec();
        LoadedImage referenceImage;
        try
        {
            referenceImage = codec.Load(referencePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"读取参考图失败：{ex.Message}");
            return 1;
        }

        ImagePixels reference = CropToMultiple(referenceImage.Pixels, model.Scale);
        int lowWidth = reference.Width / model.Scale;
        int lowHeight = reference.Height / model.Scale;

        // 低分辨率输入：Lanczos3 抗锯齿降采样（与超分模型的退化假设一致）。
        ImagePixels lowResolution = LanczosResampler.Resize(reference, lowWidth, lowHeight);

        var upscaleOptions = new UpscaleOptions
        {
            Model = model,
            TargetScale = model.Scale,
            Backend = options.GetBackend(),
            PreferFp16 = options.GetBool("--fp16", true),
            TileSize = options.GetInt("--tile", 0),
            TilePadding = options.GetInt("--pad", 0),
            PreviewMaxDimension = 0,
        };

        Console.WriteLine($"参考图  : {Path.GetFullPath(referencePath)}  {referenceImage.Width}x{referenceImage.Height}" +
                          (reference.Width != referenceImage.Width || reference.Height != referenceImage.Height
                              ? $"（裁剪至 {reference.Width}x{reference.Height}，为 {model.Scale} 的整数倍）"
                              : string.Empty));
        Console.WriteLine($"低分辨率: {lowWidth}x{lowHeight}（Lanczos3 降采样 {model.Scale}x）");
        Console.WriteLine($"模型    : {model.Id}（{model.DisplayName}，x{model.Scale}），后端={CliSupport.FormatBackendName(upscaleOptions.Backend)}，fp16={upscaleOptions.PreferFp16}");
        Console.WriteLine();

        using var service = new OnnxUpscalerService(codec);
        var watch = Stopwatch.StartNew();
        UpscaleResult result = await service.UpscaleAsync(new LoadedImage(lowResolution, sourceFormat: "LR"), upscaleOptions);
        watch.Stop();

        if (result.OutputWidth != reference.Width || result.OutputHeight != reference.Height)
        {
            Console.Error.WriteLine($"模型输出尺寸 {result.OutputWidth}x{result.OutputHeight} 与参考图 {reference.Width}x{reference.Height} 不一致。");
            return 1;
        }

        // Lanczos3 基线：同一张低分辨率图直接插值放大。
        var baselineWatch = Stopwatch.StartNew();
        ImagePixels lanczosBaseline = LanczosResampler.Resize(lowResolution, reference.Width, reference.Height);
        baselineWatch.Stop();

        string? savePath = options.Get("--output");
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            codec.Save(result.Output, savePath, SaveOptions.Default);
            Console.WriteLine($"模型输出已保存：{Path.GetFullPath(savePath)}");
        }

        double modelPsnr = ImageMetrics.Psnr(reference, result.Output);
        double modelSsim = ImageMetrics.Ssim(reference, result.Output);
        double baselinePsnr = ImageMetrics.Psnr(reference, lanczosBaseline);
        double baselineSsim = ImageMetrics.Ssim(reference, lanczosBaseline);

        Console.WriteLine($"超分耗时: {result.Duration.TotalMilliseconds:F0} ms（{result.Backend}，{result.Precision}，tile={result.TileSize}，块数={result.TileCount}，会话={service.LastSessionCreationTime.TotalMilliseconds:F0} ms）");
        Console.WriteLine($"Lanczos3 基线耗时: {baselineWatch.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine();
        Console.WriteLine($"{"方法",-16}{"PSNR(dB)",12}{"SSIM",10}");
        Console.WriteLine($"{new string('-', 38)}");
        Console.WriteLine($"{"模型 x" + model.Scale, -16}{FormatPsnr(modelPsnr),12}{modelSsim,10:F4}");
        Console.WriteLine($"{"Lanczos3 基线", -16}{FormatPsnr(baselinePsnr),12}{baselineSsim,10:F4}");
        Console.WriteLine($"{new string('-', 38)}");
        Console.WriteLine($"{"差值", -16}{FormatDelta(modelPsnr, baselinePsnr),12}{modelSsim - baselineSsim,10:+0.0000;-0.0000;0.0000}");
        Console.WriteLine();
        Console.WriteLine("说明：PSNR 在 RGB（0..255）上计算；SSIM 为亮度通道 11x11 高斯窗（σ=1.5）。");
        return 0;
    }

    private static ImagePixels CropToMultiple(ImagePixels source, int multiple)
    {
        int width = source.Width / multiple * multiple;
        int height = source.Height / multiple * multiple;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"参考图 {source.Width}x{source.Height} 小于模型倍率 {multiple}。", nameof(source));
        }

        if (width == source.Width && height == source.Height)
        {
            return source;
        }

        // 居中裁剪，避免总取左上角导致构图偏差。
        int offsetX = (source.Width - width) / 2;
        int offsetY = (source.Height - height) / 2;
        var cropped = new ImagePixels(width, height);
        byte[] sourcePixels = source.Pixels;
        byte[] destination = cropped.Pixels;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = ((offsetY + y) * source.Width) + offsetX;
            int destinationRow = y * width;
            Buffer.BlockCopy(
                sourcePixels,
                sourceRow * ImagePixels.BytesPerPixel,
                destination,
                destinationRow * ImagePixels.BytesPerPixel,
                width * ImagePixels.BytesPerPixel);
        }

        return cropped;
    }

    private static string FormatPsnr(double value) =>
        double.IsPositiveInfinity(value) ? "∞" : value.ToString("F2");

    private static string FormatDelta(double modelValue, double baselineValue)
    {
        if (double.IsPositiveInfinity(modelValue) || double.IsPositiveInfinity(baselineValue))
        {
            return "∞";
        }

        return (modelValue - baselineValue).ToString("+0.00;-0.00;0.00");
    }
}
