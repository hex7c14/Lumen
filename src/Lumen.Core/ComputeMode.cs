namespace Lumen.Core;

/// <summary>
/// 处理模式（面向兼容性）。UI 用它映射到具体后端组合：
/// <list type="bullet">
/// <item><see cref="Auto"/>：有 DirectML 用 GPU + fp16，否则 CPU + fp32。</item>
/// <item><see cref="Gpu"/>：强制 DirectML（失败时给出明确错误，便于排查显卡/驱动问题）。</item>
/// <item><see cref="CpuCompat"/>：CPU 兼容模式，fp32 + CPU 优化分块，任何支持 SSE4 的 x64 Windows 都能跑。</item>
/// </list>
/// </summary>
public enum ComputeMode
{
    Auto = 0,
    Gpu = 1,
    CpuCompat = 2,
}
