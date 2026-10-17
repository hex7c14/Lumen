using System.Diagnostics;
using Lumen.Core;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Cli.Commands;

/// <summary>
/// bench：合成一张 size×size 的测试图，重复超分，输出每块耗时、首块（含会话创建）耗时、总耗时与后端。
/// 第一次迭代是冷启动（含会话创建/模型加载），之后的迭代复用缓存会话。
/// </summary>
internal static class BenchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options = CliOptions.Parse(args);
        ModelCatalog catalog = new(AppPaths.ModelsRoot);
        ModelDescriptor? model = UpscaleCommand.ResolveModel(catalog, options.Get("--model"));
        if (model is null)
        {
            return 1;
        }

        int size = options.GetInt("--size", 512);
        int iterations = options.GetInt("--iters", 3);
        if (size is < 32 or > 4096)
        {
            Console.Error.WriteLine("--size 必须在 32..4096 之间。");
            return 2;
        }

        if (iterations < 1)
        {
            Console.Error.WriteLine("--iters 必须 ≥ 1。");
            return 2;
        }

        var codec = new SkiaImageCodec();
        bool uniformTileShape = options.GetBool("--uniform-pad", true);
        var upscaleOptions = new UpscaleOptions
        {
            Model = model,
            TargetScale = model.Scale,
            Backend = options.GetBackend(),
            PreferFp16 = options.GetBool("--fp16", true),
            TileSize = options.GetInt("--tile", 0),
            TilePadding = options.GetInt("--pad", 0),
            PreviewMaxDimension = 0,
            CpuThreads = options.GetInt("--threads", 0),
        };

        LoadedImage image = CreateSyntheticImage(size);
        Console.WriteLine($"模型    : {model.Id}（{model.DisplayName}，x{model.Scale}）");
        Console.WriteLine($"输入    : 合成图 {size}x{size}（渐变 + 棋盘边缘）→ x{model.Scale}");
        Console.WriteLine($"后端请求: {CliSupport.FormatBackendName(upscaleOptions.Backend)}   fp16={upscaleOptions.PreferFp16}   uniformPad={uniformTileShape}");
        Console.WriteLine($"迭代次数: {iterations}");
        Console.WriteLine();

        using var service = new OnnxUpscalerService(codec) { PadTilesToUniformShape = uniformTileShape };
        var coldTotals = new List<double>();
        var warmTotals = new List<double>();
        var warmTileAverages = new List<double>();
        int tileCount = 0;
        string backend = "?";
        ModelPrecision precision = ModelPrecision.Fp32;
        int tileSize = 0;
        int tilePadding = 0;

        for (int iteration = 1; iteration <= iterations; iteration++)
        {
            var watch = Stopwatch.StartNew();
            UpscaleResult result = await service.UpscaleAsync(image, upscaleOptions);
            watch.Stop();

            IReadOnlyList<TileTiming> timings = service.LastTileTimings;
            double session = service.LastSessionCreationTime.TotalMilliseconds;
            double firstTile = timings.Count > 0 ? timings[0].Elapsed.TotalMilliseconds : 0;
            double tileAverage = timings.Count > 0 ? timings.Average(t => t.Elapsed.TotalMilliseconds) : 0;

            bool cold = iteration == 1;
            Console.WriteLine(
                $"迭代 {iteration}/{iterations}{(cold ? "（冷启动，含会话创建）" : "（缓存会话）")}：" +
                $"总 {watch.Elapsed.TotalMilliseconds:F0} ms，块数 {result.TileCount}，后端 {result.Backend}，精度 {result.Precision}");
            Console.WriteLine($"  会话创建/命中 : {session:F0} ms");
            Console.WriteLine($"  首块(含会话创建): {(cold ? session + firstTile : firstTile):F0} ms");
            Console.WriteLine($"  每块耗时(ms)  : {string.Join("  ", timings.Select(t => $"{t.Index + 1}={t.Elapsed.TotalMilliseconds:F1}"))}");
            Console.WriteLine($"  块平均/总计   : {tileAverage:F1} ms / {watch.Elapsed.TotalMilliseconds:F0} ms");

            if (cold)
            {
                coldTotals.Add(watch.Elapsed.TotalMilliseconds);
                tileCount = result.TileCount;
                backend = result.Backend;
                precision = result.Precision;
                tileSize = result.TileSize;
                tilePadding = service.LastTilePadding;
            }
            else
            {
                warmTotals.Add(watch.Elapsed.TotalMilliseconds);
                warmTileAverages.Add(tileAverage);
            }

            if (iteration == 1)
            {
                Console.WriteLine();
            }
        }

        double megapixels = size * (double)model.Scale * size * model.Scale / 1_000_000.0;
        Console.WriteLine();
        Console.WriteLine($"汇总：后端={backend} 精度={precision} tile={tileSize} pad={tilePadding} 块数={tileCount} uniformPad={uniformTileShape}");
        Console.WriteLine($"  冷启动总耗时: {coldTotals[0]:F0} ms");
        if (warmTotals.Count > 0)
        {
            Console.WriteLine(
                $"  稳定态总耗时: 平均 {warmTotals.Average():F0} ms，最快 {warmTotals.Min():F0} ms，最慢 {warmTotals.Max():F0} ms" +
                $"（{warmTotals.Count} 次）");
            Console.WriteLine($"  稳定态块平均: {warmTileAverages.Average():F1} ms/块");
            Console.WriteLine($"  吞吐        : {megapixels / (warmTotals.Average() / 1000.0):F2} MPix/s（输出像素）");
        }

        return 0;
    }

    /// <summary>确定性的合成测试图：渐变 + 棋盘状高频边缘 + 平滑色块，避免随机噪声导致耗时抖动。</summary>
    internal static LoadedImage CreateSyntheticImage(int size)
    {
        var pixels = new ImagePixels(size, size);
        byte[] buffer = pixels.Pixels;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = ((y * size) + x) * ImagePixels.BytesPerPixel;
                int checkerX = (x / 8) & 1;
                int checkerY = (y / 8) & 1;
                bool bright = checkerX == checkerY;
                buffer[index] = (byte)(bright ? 235 : 24);
                buffer[index + 1] = (byte)((x * 255) / Math.Max(1, size - 1));
                buffer[index + 2] = (byte)((y * 255) / Math.Max(1, size - 1));
                buffer[index + 3] = 255;
            }
        }

        return new LoadedImage(pixels, sourceFormat: "SYNTHETIC");
    }
}
