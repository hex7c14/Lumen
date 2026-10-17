using System.Diagnostics;
using Lumen.Core;
using Lumen.Core.Batch;
using Lumen.Core.Imaging;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;

namespace Lumen.Cli.Commands;

/// <summary>
/// batch：多图/文件夹顺序超分，统一命名与冲突策略、失败隔离，逐项打印结果。
/// 输出目录为空时写到每张源图旁边。
/// </summary>
internal static class BatchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options = CliOptions.Parse(args);
        string? rawInputs = options.Get("--inputs");
        if (string.IsNullOrWhiteSpace(rawInputs))
        {
            Console.Error.WriteLine(
                "用法：Lumen.Cli batch --inputs <目录|文件1;文件2|通配符> [--output-dir <目录>] [--model <id>] " +
                "[--scale 4] [--format png|jpeg|webp|bmp] [--quality 92] [--pattern \"{name}_{scale}x\"] " +
                "[--conflict rename|overwrite|skip] [--backend auto|gpu|cpu] [--fp16 on|off] [--uniform-pad on|off] " +
                "[--threads N] [--cpu-compat on|off] [--keep-preview on|off] [--preview 1600]");
            return 2;
        }

        ModelCatalog catalog = new(AppPaths.ModelsRoot);
        ModelDescriptor? model = UpscaleCommand.ResolveModel(catalog, options.Get("--model"));
        if (model is null)
        {
            return 1;
        }

        var codec = new SkiaImageCodec();
        IReadOnlyList<string> inputs = ExpandInputs(rawInputs, codec);
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine($"没有找到可处理的图片：{rawInputs}");
            return 2;
        }

        int targetScale = options.GetInt("--scale", model.Scale);
        if (targetScale < 1 || targetScale > model.Scale)
        {
            Console.Error.WriteLine($"--scale 必须在 1..{model.Scale} 之间（模型固有倍率 {model.Scale}）。");
            return 2;
        }

        OutputFormat format;
        try
        {
            format = CliSupport.ResolveOutputFormat(options.Get("--format"), "out.png");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        OutputConflictPolicy conflictPolicy;
        switch ((options.Get("--conflict") ?? "rename").ToLowerInvariant())
        {
            case "rename":
            case "autorename":
                conflictPolicy = OutputConflictPolicy.AutoRename;
                break;
            case "overwrite":
                conflictPolicy = OutputConflictPolicy.Overwrite;
                break;
            case "skip":
                conflictPolicy = OutputConflictPolicy.Skip;
                break;
            default:
                Console.Error.WriteLine("--conflict 必须是 rename / overwrite / skip。");
                return 2;
        }

        bool uniformPadding = options.GetBool("--uniform-pad", true);
        bool keepPreview = options.GetBool("--keep-preview", false);
        ExecutionBackend backend = options.GetBackend();
        var upscaleOptions = new UpscaleOptions
        {
            Model = model,
            TargetScale = targetScale,
            Backend = backend,
            PreferFp16 = options.GetBool("--fp16", true),
            TileSize = options.GetInt("--tile", 0),
            TilePadding = options.GetInt("--pad", 0),
            PreviewMaxDimension = keepPreview ? options.GetInt("--preview", 1600) : 0,
            // 与 UI 一致的批量安全上限（400M 像素 ≈ 1.6GB RGBA 对批量太激进）。
            MaxOutputPixels = options.GetInt("--max-output-pixels", 150_000_000),
            CpuThreads = options.GetInt("--threads", 0),
            CpuCompatibilityMode = options.GetBool("--cpu-compat", false),
        };

        var batchOptions = new BatchOptions
        {
            Upscale = upscaleOptions,
            Save = new SaveOptions(format, options.GetInt("--quality", 92)),
            OutputDirectory = options.Get("--output-dir"),
            FileNamePattern = options.Get("--pattern") ?? "{name}_{scale}x",
            ConflictPolicy = conflictPolicy,
            // CLI 批量默认不生成预览（省内存/省时）；--keep-preview on 可打开。
            KeepPreview = keepPreview,
            PreviewMaxDimension = keepPreview ? options.GetInt("--preview", 1600) : 0,
        };

        try
        {
            batchOptions.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"参数无效：{ex.Message}");
            return 2;
        }

        using var engine = new OnnxUpscalerService(codec) { PadTilesToUniformShape = uniformPadding };
        using var batchService = new BatchUpscalerService(engine, codec);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("收到 Ctrl+C，正在取消…");
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        bool interactive = !Console.IsOutputRedirected;
        var reporter = new ConsoleBatchProgress(interactive);
        try
        {
            Console.WriteLine($"模型    : {model.Id}（{model.DisplayName}，x{model.Scale}）");
            Console.WriteLine($"输入    : {inputs.Count} 张（{Path.GetFileName(rawInputs)}）");
            Console.WriteLine($"输出    : {(string.IsNullOrWhiteSpace(batchOptions.OutputDirectory) ? "源图所在目录" : Path.GetFullPath(batchOptions.OutputDirectory))}");
            Console.WriteLine($"命名    : {batchOptions.FileNamePattern}{BatchNaming.ExtensionFor(format)}   冲突={conflictPolicy}");
            Console.WriteLine($"后端    : {CliSupport.FormatBackendName(upscaleOptions.Backend)}   fp16={upscaleOptions.PreferFp16}   uniformPad={uniformPadding}");
            Console.WriteLine();

            var watch = Stopwatch.StartNew();
            IReadOnlyList<BatchItemResult> results =
                await batchService.ProcessAsync(inputs, batchOptions, reporter, cts.Token);
            watch.Stop();
            reporter.FinishLine();

            Console.WriteLine();
            for (int i = 0; i < results.Count; i++)
            {
                BatchItemResult item = results[i];
                Console.WriteLine(FormatItemLine(i + 1, results.Count, item, interactive));
            }

            int completed = results.Count(r => r.Status == BatchItemStatus.Completed);
            int failed = results.Count(r => r.Status == BatchItemStatus.Failed);
            int skipped = results.Count(r => r.Status == BatchItemStatus.Skipped);
            int canceled = results.Count(r => r.Status == BatchItemStatus.Canceled);
            Console.WriteLine();
            Console.WriteLine($"汇总：共 {results.Count} 张，成功 {completed} / 失败 {failed} / 跳过 {skipped}" +
                              (canceled > 0 ? $" / 取消 {canceled}" : string.Empty) +
                              $"；总耗时 {watch.Elapsed.TotalSeconds:F1}s");

            if (canceled > 0)
            {
                return 1;
            }

            return failed > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            reporter.FinishLine();
            Console.Error.WriteLine("已取消。");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string FormatItemLine(int index, int count, BatchItemResult item, bool interactive)
    {
        string name = Path.GetFileName(item.InputPath);
        string head = $"[{index}/{count}] {name}";
        return item.Status switch
        {
            BatchItemStatus.Completed =>
                $"{head} {item.InputWidth}x{item.InputHeight} → 完成 {item.Duration.TotalSeconds:F1}s -> {item.OutputPath}",
            BatchItemStatus.Failed => $"{head} → 失败：{item.ErrorMessage}",
            BatchItemStatus.Skipped => $"{head} → 跳过（已有输出）-> {item.OutputPath}",
            BatchItemStatus.Canceled => $"{head} → 已取消",
            _ => $"{head} → {item.Status}",
        };
    }

    /// <summary>展开输入：目录递归扫描、分号分隔、通配符；按绝对路径去重并排序。</summary>
    internal static IReadOnlyList<string> ExpandInputs(string rawInputs, IImageCodec codec)
    {
        var extensions = new HashSet<string>(codec.SupportedDecodeExtensions, StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string part in rawInputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(part))
            {
                files.AddRange(
                    Directory.EnumerateFiles(part, "*", SearchOption.AllDirectories)
                        .Where(file => extensions.Contains(Path.GetExtension(file))));
                continue;
            }

            if (part.Contains('*') || part.Contains('?'))
            {
                string directory = Path.GetDirectoryName(part) is { Length: > 0 } dir ? dir : ".";
                string pattern = Path.GetFileName(part);
                if (Directory.Exists(directory))
                {
                    files.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
                }

                continue;
            }

            files.Add(part);
        }

        return files
            .Select(file => Path.GetFullPath(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>批处理进度打印：交互终端用一行 \r 刷新当前项，重定向时只在项切换/结束时打印。</summary>
    private sealed class ConsoleBatchProgress : IProgress<BatchProgress>
    {
        private readonly bool _interactive;
        private readonly Dictionary<int, (Stopwatch Watch, bool Started)> _items = [];
        private int _lastPrintedItem = -1;
        private BatchItemStage _lastStage = (BatchItemStage)(-1);

        public ConsoleBatchProgress(bool interactive)
        {
            _interactive = interactive;
        }

        public void Report(BatchProgress value)
        {
            if (!_items.TryGetValue(value.ItemIndex, out (Stopwatch Watch, bool Started) item))
            {
                item = (Stopwatch.StartNew(), false);
                _items[value.ItemIndex] = item;
            }

            if (!item.Started && value.Stage == BatchItemStage.Loading)
            {
                _items[value.ItemIndex] = (item.Watch, true);
                if (_interactive)
                {
                    Console.WriteLine($"[{value.ItemIndex + 1}/{value.ItemCount}] {Path.GetFileName(value.InputPath)}");
                }
            }

            if (value.Stage is BatchItemStage.Completed or BatchItemStage.Failed or BatchItemStage.Skipped or BatchItemStage.Canceled)
            {
                item.Watch.Stop();
                _items[value.ItemIndex] = (item.Watch, true);
                if (_interactive)
                {
                    string status = value.Stage switch
                    {
                        BatchItemStage.Completed => $"完成 {item.Watch.Elapsed.TotalSeconds:F1}s",
                        BatchItemStage.Failed => $"失败：{value.Message}",
                        BatchItemStage.Skipped => "跳过",
                        _ => "已取消",
                    };
                    Console.WriteLine($"  → {status}");
                }

                return;
            }

            if (_interactive && (value.Stage != _lastStage || value.ItemIndex != _lastPrintedItem || value.Stage == BatchItemStage.Upscaling))
            {
                _lastStage = value.Stage;
                _lastPrintedItem = value.ItemIndex;
                string tileText = value.TilesTotal > 0 ? $" 块 {value.TilesCompleted}/{value.TilesTotal}" : string.Empty;
                Console.Write($"\r  整体 {value.OverallFraction * 100,5:F1}%  当前 {value.ItemFraction * 100,5:F1}%{tileText}  ");
            }
        }

        public void FinishLine()
        {
            if (_interactive)
            {
                Console.WriteLine();
            }
        }
    }
}
