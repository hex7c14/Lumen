using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lumen.Core.Diagnostics;

/// <summary>计算环境信息（供 UI 显示“GPU / CPU 后端”）。</summary>
public interface IComputeEnvironment
{
    /// <summary>当前 ONNX Runtime 构建是否带 DirectML EP。</summary>
    bool DirectMlAvailable { get; }

    /// <summary>已注册的显示适配器名称（可能包含核显）。</summary>
    IReadOnlyList<string> GpuNames { get; }

    /// <summary>首选的独立显卡名称；识别不到时为 null。</summary>
    string? PrimaryGpuName { get; }

    /// <summary>CPU 型号。</summary>
    string? CpuName { get; }

    string Summary { get; }
}

/// <summary>通过 ONNX Runtime Provider 列表与注册表探测本机计算环境。</summary>
public sealed class ComputeEnvironment : IComputeEnvironment
{
    private ComputeEnvironment(bool directMlAvailable, IReadOnlyList<string> gpuNames, string? cpuName)
    {
        DirectMlAvailable = directMlAvailable;
        GpuNames = gpuNames;
        CpuName = cpuName;
        PrimaryGpuName = PickPrimaryGpu(gpuNames);
    }

    public bool DirectMlAvailable { get; }

    public IReadOnlyList<string> GpuNames { get; }

    public string? PrimaryGpuName { get; }

    public string? CpuName { get; }

    public string Summary
    {
        get
        {
            string gpu = PrimaryGpuName ?? (DirectMlAvailable ? "DirectML 设备" : "无可用 GPU");
            string cpu = CpuName ?? "CPU";
            return DirectMlAvailable ? $"{gpu} · DirectML（CPU 回退：{cpu}）" : $"{cpu}（CPU 推理）";
        }
    }

    public static ComputeEnvironment Detect()
    {
        // 本仓库固定使用 Microsoft.ML.OnnxRuntime.DirectML 构建，只要 DirectML.dll 能加载，
        // 就说明运行时具备 DirectML EP 的前提；真正不可用时推理会话创建会抛错并回退 CPU。
        bool dml = ProbeDirectMl();

        IReadOnlyList<string> gpus = QueryGpuNames();
        string? cpu = QueryCpuName();
        return new ComputeEnvironment(dml, gpus, cpu);
    }

    private static bool ProbeDirectMl()
    {
        try
        {
            // DirectML.dll 由 Microsoft.AI.DirectML 的构建目标复制到输出目录。
            if (NativeLibrary.TryLoad("DirectML.dll", out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"探测 DirectML 失败：{ex.Message}");
        }

        return false;
    }

    private static IReadOnlyList<string> QueryGpuNames()
    {
        var names = new List<string>();
        try
        {
            // 显示适配器类 GUID，枚举其子键即可拿到所有显卡的 DriverDesc。
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is not null)
            {
                foreach (string subKeyName in classKey.GetSubKeyNames())
                {
                    if (subKeyName.Length != 4 || !subKeyName.All(char.IsDigit))
                    {
                        continue;
                    }

                    using RegistryKey? adapter = classKey.OpenSubKey(subKeyName);
                    if (adapter?.GetValue("DriverDesc") is string description && !string.IsNullOrWhiteSpace(description))
                    {
                        names.Add(description.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取显卡信息失败：{ex.Message}");
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? QueryCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? PickPrimaryGpu(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        // 优先独立显卡：NVIDIA / AMD Radeon RX / Intel Arc。
        string[] dedicatedMarkers = ["NVIDIA", "GeForce", "RTX", "GTX", "Radeon RX", "Radeon Pro", "Arc"];
        foreach (string marker in dedicatedMarkers)
        {
            string? match = names.FirstOrDefault(n => n.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return names[0];
    }
}
