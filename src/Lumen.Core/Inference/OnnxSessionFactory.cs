using System.Diagnostics;
using Lumen.Core.Diagnostics;
using Lumen.Core.Models;
using Lumen.Core.Upscaling;
using Microsoft.ML.OnnxRuntime;

namespace Lumen.Core.Inference;

/// <summary>
/// 创建 ONNX Runtime 推理会话：选择 DirectML/CPU 执行提供器、处理 Auto 回退、读取并校验元数据。
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>
    /// 按请求的后端创建会话。<paramref name="precision"/> 必须是调用方解析过的实际权重精度。
    /// <paramref name="cpuThreads"/> 仅 CPU 后端使用：&gt;0 时设置 IntraOpNumThreads，0 表示由 ORT 决定。
    /// <paramref name="fallbackNote"/> 非 null 表示 Auto 模式从 DirectML 回退到了 CPU，内容为失败原因。
    /// </summary>
    internal static OnnxSessionHandle Create(
        ModelDescriptor model,
        ModelPrecision precision,
        ExecutionBackend backend,
        int deviceId,
        int cpuThreads,
        out string? fallbackNote)
    {
        ArgumentNullException.ThrowIfNull(model);
        fallbackNote = null;

        switch (backend)
        {
            case ExecutionBackend.Cpu:
            {
                // CPU 后端强制 fp32：fp16 权重在 CPU 上通常更慢，而且需要额外的 Half 转换。
                return CreateSession(model, ModelPrecision.Fp32, ExecutionBackend.Cpu, deviceId, cpuThreads);
            }

            case ExecutionBackend.DirectML:
            {
                return CreateSession(model, precision, ExecutionBackend.DirectML, deviceId, cpuThreads);
            }

            case ExecutionBackend.Auto:
            default:
            {
                try
                {
                    return CreateSession(model, precision, ExecutionBackend.DirectML, deviceId, cpuThreads);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    fallbackNote = ex.Message;
                    AppLog.Warn($"DirectML 会话创建失败，回退 CPU：{ex.Message}");
                    return CreateSession(model, ModelPrecision.Fp32, ExecutionBackend.Cpu, deviceId, cpuThreads);
                }
            }
        }
    }

    private static OnnxSessionHandle CreateSession(
        ModelDescriptor model,
        ModelPrecision precision,
        ExecutionBackend backend,
        int deviceId,
        int cpuThreads)
    {
        string modelPath = model.ResolveModelPath(precision);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"模型权重文件不存在：{modelPath}", modelPath);
        }

        var stopwatch = Stopwatch.StartNew();
        using var options = new SessionOptions
        {
            // 全部图优化；DirectML 要求关闭内存复用模式（EP 会自己管理设备内存）。
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,
            // 单线程顺序执行：分块推理是串行的，避免 DirectML 上多线程争抢设备。
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        if (backend == ExecutionBackend.DirectML)
        {
            options.AppendExecutionProvider_DML(deviceId);
        }
        else
        {
            options.AppendExecutionProvider_CPU(0);
            // 0 表示交给 ONNX Runtime 按核数决定；CPU 兼容模式可显式限制线程数。
            options.InterOpNumThreads = 0;
            options.IntraOpNumThreads = cpuThreads > 0 ? cpuThreads : 0;
        }

        InferenceSession session;
        try
        {
            session = new InferenceSession(modelPath, options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"创建 ONNX 推理会话失败（后端 {backend}，权重 {Path.GetFileName(modelPath)}）：{ex.Message}",
                ex);
        }

        try
        {
            (string inputName, bool inputFp16, string outputName, bool outputFp16) = ReadMetadata(session, modelPath, backend);
            stopwatch.Stop();

            var handle = new OnnxSessionHandle
            {
                Session = session,
                InputName = inputName,
                OutputName = outputName,
                InputIsFloat16 = inputFp16,
                OutputIsFloat16 = outputFp16,
                EffectiveBackend = backend,
                Precision = precision,
                ModelPath = modelPath,
                BackendDisplayName = backend == ExecutionBackend.DirectML ? "DirectML" : "CPU",
            };

            AppLog.Info(
                $"会话就绪：模型={model.Id} 后端={handle.BackendDisplayName} 精度={precision} " +
                $"输入={inputName}({(inputFp16 ? "float16" : "float32")}) 输出={outputName}({(outputFp16 ? "float16" : "float32")}) " +
                $"设备={deviceId} 线程={(backend == ExecutionBackend.Cpu ? (cpuThreads > 0 ? cpuThreads.ToString() : "自动") : "-")} " +
                $"耗时={stopwatch.ElapsedMilliseconds}ms");
            return handle;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 读取元数据并校验契约：单输入单输出、NCHW、3 通道、动态 H/W、元素类型 float / float16。
    /// </summary>
    private static (string InputName, bool InputFp16, string OutputName, bool OutputFp16) ReadMetadata(
        InferenceSession session,
        string modelPath,
        ExecutionBackend backend)
    {
        string modelLabel = $"{Path.GetFileName(modelPath)}（后端 {backend}）";
        if (session.InputMetadata.Count != 1)
        {
            throw new NotSupportedException(
                $"模型 {modelLabel} 有 {session.InputMetadata.Count} 个输入；引擎只支持单输入超分模型。");
        }

        if (session.OutputMetadata.Count < 1)
        {
            throw new NotSupportedException($"模型 {modelLabel} 没有输出。");
        }

        string inputName = session.InputMetadata.Keys.First();
        NodeMetadata input = session.InputMetadata[inputName];
        bool inputFp16 = ValidateTensorMetadata(input, modelLabel, "输入");

        int[] dimensions = input.Dimensions;
        if (dimensions.Length != 4)
        {
            throw new NotSupportedException($"模型 {modelLabel} 的输入必须是 4 维 NCHW，实际 {dimensions.Length} 维。");
        }

        if (dimensions[1] != 3 && dimensions[1] != -1)
        {
            throw new NotSupportedException($"模型 {modelLabel} 的输入通道数必须是 3（RGB），实际 {dimensions[1]}。");
        }

        if (dimensions[2] > 0 || dimensions[3] > 0)
        {
            throw new NotSupportedException(
                $"模型 {modelLabel} 的输入 H/W 是固定尺寸（{dimensions[2]}x{dimensions[3]}）；" +
                "分块推理要求 dynamicAxes=true（见 docs/ARCHITECTURE.md §4）。");
        }

        if (dimensions[0] > 0 && dimensions[0] != 1)
        {
            throw new NotSupportedException($"模型 {modelLabel} 的 batch 必须是 1 或动态，实际 {dimensions[0]}。");
        }

        string outputName = session.OutputMetadata.Keys.First();
        NodeMetadata output = session.OutputMetadata[outputName];
        bool outputFp16 = ValidateTensorMetadata(output, modelLabel, "输出");

        return (inputName, inputFp16, outputName, outputFp16);
    }

    private static bool ValidateTensorMetadata(NodeMetadata metadata, string modelLabel, string role)
    {
        Type? elementType = metadata.ElementType;
        if (elementType == typeof(float))
        {
            return false;
        }

        if (elementType == typeof(Float16))
        {
            return true;
        }

        throw new NotSupportedException(
            $"模型 {modelLabel} 的{role}元素类型 {elementType?.FullName ?? "未知"} 不受支持；只支持 float32 / float16。");
    }
}
