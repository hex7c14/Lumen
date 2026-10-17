namespace Lumen.Core.Upscaling;

/// <summary>
/// 输入坐标系下的一个分块矩形。<see cref="X"/>/<see cref="Y"/> 是块的“核心”左上角，
/// 推理时会向四周各扩展 padding 像素作为上下文，输出只取核心区域，避免接缝。
/// </summary>
public readonly record struct TileRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>一次运行的分块计划：块尺寸、边距以及每个块的核心矩形。</summary>
public sealed class TilePlan
{
    public required int ImageWidth { get; init; }

    public required int ImageHeight { get; init; }

    /// <summary>每块核心区域的宽度（输入像素）。</summary>
    public required int TileWidth { get; init; }

    /// <summary>每块核心区域的高度（输入像素）。</summary>
    public required int TileHeight { get; init; }

    /// <summary>水平方向的上下文边距（输入像素）。</summary>
    public required int PaddingX { get; init; }

    /// <summary>垂直方向的上下文边距（输入像素）。</summary>
    public required int PaddingY { get; init; }

    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required IReadOnlyList<TileRect> Tiles { get; init; }

    public int Count => Tiles.Count;
}

/// <summary>
/// 分块规划。把图片切成相互重叠（核心区域按 stride 前进）的块：每块的输入张量向四周扩展
/// <see cref="PaddingX"/>/<see cref="PaddingY"/> 像素，推理后只保留核心区域，所以接缝处两侧
/// 都有足够的感受野上下文；最后一块贴住右/下边缘，块尺寸保持完整，避免模型对残缺尺寸的特殊处理。
/// </summary>
public static class TilePlanner
{
    /// <summary>自动分块时允许的最大块边长。</summary>
    public const int MaxTileSize = 4096;

    /// <summary>
    /// 自动选择分块边长：DirectML + RRDB 用 256（速度/质量平衡），DirectML 其他架构 384，CPU 128。
    /// </summary>
    public static int ResolveAutoTileSize(ExecutionBackend backend, string? arch)
    {
        if (backend == ExecutionBackend.Cpu)
        {
            return 128;
        }

        return IsRrdb(arch) ? 256 : 384;
    }

    /// <summary>
    /// 结合图片尺寸的自动分块（依据 docs/PERFORMANCE.md §7 的 C# 实测，含进程峰值内存）：
    /// DirectML 小图（长边 ≤512）单块，大图 384（512 块在 8GB 上开始非线性变慢）；
    /// CPU 上重叠分块的重复计算占比很高：长边 ≤512 用单块（CUGAN 512 图 4.3s vs 多块 12.2s），
    /// 长边 >512 用 512 块（单块 768/1024 峰值内存 5.5–13GB，anime-6b 1024 单块直接 OOM；512 块约 3.3GB）。
    /// </summary>
    public static int ResolveAutoTileSize(ExecutionBackend backend, string? arch, int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);

        int longEdge = Math.Max(imageWidth, imageHeight);
        if (backend == ExecutionBackend.Cpu)
        {
            return longEdge <= 512 ? longEdge : 512;
        }

        if (longEdge <= 512)
        {
            return longEdge;
        }

        // 实测 RRDB（x4plus）与 SRVGG（x4v3）在 1024/1600 上都是 384 最优；512 块在 8GB 上
        // 出现明显非线性变慢（显存压力），768 更会 thrash。
        return 384;
    }

    /// <summary>
    /// CPU 兼容模式的保守分块：固定 256（docs/PERFORMANCE.md §7 实测峰值内存
    /// CUGAN 0.87GB / anime-6b 2.18GB，8GB RAM 机器可安全运行；比 512/1024 单块低 2–8 倍内存）。
    /// 稳定优先，速度比正常 CPU 模式慢（512 图约 12.2s vs 4.3s）。
    /// </summary>
    public static int ResolveCpuCompatibilityTileSize(string? arch)
    {
        return 256;
    }

    /// <summary>
    /// 自动选择上下文边距：RRDB 默认 24；其余取 max(8, min(32, tile/8))。
    /// </summary>
    public static int ResolveAutoPadding(int tileSize, string? arch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        if (IsRrdb(arch))
        {
            return Math.Max(1, Math.Min(24, (tileSize - 1) / 2));
        }

        int padding = Math.Max(8, Math.Min(32, tileSize / 8));
        return Math.Max(1, Math.Min(padding, (tileSize - 1) / 2));
    }

    /// <summary>
    /// 生成分块计划。<paramref name="tileSize"/> 与 <paramref name="padding"/> 必须已经解析成具体值（&gt; 0）。
    /// </summary>
    public static TilePlan Create(int imageWidth, int imageHeight, int tileSize, int padding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        if (tileSize > MaxTileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, $"分块边长不能超过 {MaxTileSize}。");
        }

        int tileWidth = Math.Min(tileSize, imageWidth);
        int tileHeight = Math.Min(tileSize, imageHeight);
        int padX = ResolveAxisPadding(imageWidth, tileWidth, padding, nameof(padding));
        int padY = ResolveAxisPadding(imageHeight, tileHeight, padding, nameof(padding));

        int[] xs = BuildAxis(imageWidth, tileWidth, padX);
        int[] ys = BuildAxis(imageHeight, tileHeight, padY);

        var tiles = new TileRect[xs.Length * ys.Length];
        int index = 0;
        for (int row = 0; row < ys.Length; row++)
        {
            for (int column = 0; column < xs.Length; column++)
            {
                tiles[index++] = new TileRect(xs[column], ys[row], tileWidth, tileHeight);
            }
        }

        return new TilePlan
        {
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            PaddingX = padX,
            PaddingY = padY,
            Columns = xs.Length,
            Rows = ys.Length,
            Tiles = tiles,
        };
    }

    private static bool IsRrdb(string? arch) =>
        string.Equals(arch, "rrdb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 单轴边距：整图放得下时最多取块边长的 1/4（小图不浪费上下文）；
    /// 需要多块时 stride = tile - 2*pad 必须为正，否则说明用户给的边距过大。
    /// </summary>
    private static int ResolveAxisPadding(int dimension, int tile, int padding, string parameterName)
    {
        if (dimension <= tile)
        {
            return Math.Min(padding, tile / 4);
        }

        if (padding * 2 >= tile)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                padding,
                $"分块边距必须小于块边长的一半（tile={tile}, pad={padding}），否则分块之间没有前进量。");
        }

        return padding;
    }

    /// <summary>
    /// 单轴块起点。公式与 docs/ARCHITECTURE.md §5.2 一致：
    /// stride = tile - 2*pad，nx = ceil((dim - 2*pad) / stride)，x_i = min(i*stride, dim - tile)。
    /// </summary>
    private static int[] BuildAxis(int dimension, int tile, int pad)
    {
        if (dimension <= tile)
        {
            return [0];
        }

        int stride = tile - (2 * pad);
        if (stride <= 0)
        {
            throw new InvalidOperationException($"分块参数无效：tile={tile}, pad={pad}。");
        }

        int count = (int)Math.Ceiling((dimension - (2.0 * pad)) / stride);
        if (count < 1)
        {
            count = 1;
        }

        int lastStart = dimension - tile;
        var starts = new int[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = Math.Min(i * stride, lastStart);
        }

        // 理论上前一块已经贴边时最后几块可能被夹到同一位置，去掉重复项。
        int unique = 1;
        for (int i = 1; i < count; i++)
        {
            if (starts[i] != starts[unique - 1])
            {
                starts[unique++] = starts[i];
            }
        }

        return unique == count ? starts : starts[..unique];
    }
}
