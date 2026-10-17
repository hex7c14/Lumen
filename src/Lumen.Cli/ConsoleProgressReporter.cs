using Lumen.Core.Upscaling;

namespace Lumen.Cli;

/// <summary>把引擎进度打到控制台：重定向时按 10% 一行，交互终端用单行 \r 刷新。</summary>
internal sealed class ConsoleProgressReporter : IProgress<UpscaleProgress>
{
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private double _lastPrintedFraction = -1;
    private UpscaleStage _lastStage = (UpscaleStage)(-1);

    public void Report(UpscaleProgress value)
    {
        bool stageChanged = value.Stage != _lastStage;
        bool progressJumped = value.Fraction - _lastPrintedFraction >= (_interactive ? 0.02 : 0.1);
        if (!stageChanged && !progressJumped && value.Fraction < 1)
        {
            return;
        }

        _lastPrintedFraction = value.Fraction;
        _lastStage = value.Stage;
        string line =
            $"[{value.Fraction * 100,5:F1}%] {DescribeStage(value.Stage)}" +
            (value.TilesTotal > 0 ? $" {value.TilesCompleted}/{value.TilesTotal}" : string.Empty) +
            $"  {value.Elapsed.TotalSeconds:F1}s" +
            (string.IsNullOrWhiteSpace(value.Message) ? string.Empty : $"  {value.Message}");

        if (_interactive)
        {
            Console.Write("\r" + line.PadRight(Math.Min(120, Math.Max(line.Length, 80))));
            if (value.Stage == UpscaleStage.Completed)
            {
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    public void FinishLine()
    {
        if (_interactive)
        {
            Console.WriteLine();
        }
    }

    private static string DescribeStage(UpscaleStage stage) => stage switch
    {
        UpscaleStage.Preparing => "准备",
        UpscaleStage.Inference => "推理",
        UpscaleStage.PostProcessing => "后处理",
        UpscaleStage.Completed => "完成",
        _ => stage.ToString(),
    };
}
