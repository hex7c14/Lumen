using System.Text;
using Lumen.Cli.Commands;
using Lumen.Core;
using Lumen.Core.Diagnostics;
using Lumen.Core.Imaging;
using Lumen.Core.Models;

namespace Lumen.Cli;

/// <summary>
/// 命令行入口。不依赖 WinUI，用来在控制台里验证引擎、跑基准与质量评估。
/// 子命令：list / info / upscale / bench / quality。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppLog.Info($"Lumen.Cli 启动：{string.Join(' ', args)}");

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => ListModels(),
                "info" => ImageInfo(GetOption(args, "--input")),
                "upscale" => await UpscaleCommand.RunAsync(args),
                "batch" => await BatchCommand.RunAsync(args),
                "bench" => await BenchCommand.RunAsync(args),
                "quality" => await QualityCommand.RunAsync(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            AppLog.Error("Cli 命令失败。", ex);
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 2;
    }

    private static int ListModels()
    {
        var catalog = new ModelCatalog(AppPaths.ModelsRoot);
        Console.WriteLine($"模型目录：{catalog.ModelsRoot}");
        Console.WriteLine($"默认模型：{catalog.Default?.Id ?? "（无）"}");
        if (catalog.Models.Count == 0)
        {
            Console.WriteLine("未找到任何模型。请先运行 tools/model-pipeline 获取模型。");
            return 1;
        }

        Console.WriteLine();
        foreach (ModelDescriptor model in catalog.Models)
        {
            string tier = model.QualityTier switch
            {
                ModelQualityTier.Fast => "极速",
                ModelQualityTier.Balanced => "均衡",
                ModelQualityTier.Quality => "质量",
                _ => "?",
            };
            Console.WriteLine(
                $"  {model.Id,-28} {model.DisplayName,-12} x{model.Scale}  {tier}  " +
                $"{(model.HasFp16 ? "fp32+fp16" : "fp32")}  {model.SizeBytes / 1024.0 / 1024.0:F1} MB");
            if (!string.IsNullOrWhiteSpace(model.Description))
            {
                Console.WriteLine($"      {model.Description}");
            }
        }

        return 0;
    }

    private static int ImageInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("缺少 --input 参数。");
            return 2;
        }

        var codec = new SkiaImageCodec();
        LoadedImage image = codec.Load(path);
        Console.WriteLine($"文件：{Path.GetFullPath(path)}");
        Console.WriteLine($"格式：{image.SourceFormat}");
        Console.WriteLine($"尺寸：{image.Width} x {image.Height}");
        Console.WriteLine($"透明通道：{(image.HasTransparency ? "有" : "无")}");
        Console.WriteLine($"方向：{image.OriginalOrientation}");
        Console.WriteLine($"像素缓冲：{image.Pixels.Pixels.LongLength / 1024.0 / 1024.0:F2} MB");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Lumen.Cli — Lumen 超分引擎命令行工具

            用法：
              Lumen.Cli list                                      列出模型与默认模型
              Lumen.Cli info --input <图片>                       查看图像尺寸/格式/透明通道
              Lumen.Cli upscale --input <图片> --output <输出> --model <id> [--scale 4]
                                [--backend auto|gpu|cpu] [--fp16 on|off] [--tile N] [--pad N]
                                [--save-format png|jpeg|webp|bmp] [--quality 92] [--preview 2560]
                                [--uniform-pad on|off]
              Lumen.Cli bench --model <id> [--size 512] [--iters 3] [--backend auto|gpu|cpu]
                                [--fp16 on|off] [--tile N] [--pad N] [--uniform-pad on|off]
              Lumen.Cli batch --inputs <目录|文件1;文件2|通配符> [--output-dir <目录>] [--model <id>]
                                [--scale 4] [--format png|jpeg|webp|bmp] [--quality 92]
                                [--pattern "{name}_{scale}x"] [--conflict rename|overwrite|skip]
                                [--backend auto|gpu|cpu] [--fp16 on|off] [--uniform-pad on|off]
                                [--threads N] [--cpu-compat on|off] [--keep-preview on|off] [--preview 1600]
              Lumen.Cli quality --model <id> --reference <图片>   参考图降采样后超分，算 PSNR/SSIM 与 Lanczos3 基线
                                [--backend auto|gpu|cpu] [--fp16 on|off]

            模型目录可用环境变量 LUMEN_MODELS_ROOT 覆盖。
            """);
    }
}
