using Lumen.Core.Upscaling;

namespace Lumen.Core.Tests;

public sealed class TilePlannerTests
{
    public static TheoryData<int, int, int, int> StandardCases => new()
    {
        { 1600, 1067, 256, 24 },
        { 512, 512, 256, 24 },
        { 1000, 800, 256, 24 },
        { 513, 257, 128, 16 },
        { 300, 200, 256, 24 },
        { 127, 93, 256, 24 },
        { 1, 1, 256, 24 },
        { 2048, 2048, 384, 32 },
        { 100, 400, 64, 8 },
        { 4000, 3000, 512, 64 },
    };

    [Theory]
    [MemberData(nameof(StandardCases))]
    public void Tiles_CoverWholeImageWithoutGaps(int width, int height, int tile, int padding)
    {
        TilePlan plan = TilePlanner.Create(width, height, tile, padding);

        Assert.Equal(width, plan.ImageWidth);
        Assert.Equal(height, plan.ImageHeight);
        Assert.Equal(Math.Min(tile, width), plan.TileWidth);
        Assert.Equal(Math.Min(tile, height), plan.TileHeight);
        Assert.Equal(plan.Columns * plan.Rows, plan.Count);
        Assert.NotEmpty(plan.Tiles);

        AssertAxisCovers(plan.Tiles.Select(t => (t.X, t.Width)).ToList(), width, plan.TileWidth);
        AssertAxisCovers(plan.Tiles.Select(t => (t.Y, t.Height)).ToList(), height, plan.TileHeight);

        foreach (TileRect rect in plan.Tiles)
        {
            Assert.InRange(rect.X, 0, width - plan.TileWidth);
            Assert.InRange(rect.Y, 0, height - plan.TileHeight);
            Assert.Equal(plan.TileWidth, rect.Width);
            Assert.Equal(plan.TileHeight, rect.Height);
            Assert.True(rect.Right <= width);
            Assert.True(rect.Bottom <= height);
        }
    }

    [Theory]
    [MemberData(nameof(StandardCases))]
    public void Tiles_LastTileSticksToRightAndBottomEdge(int width, int height, int tile, int padding)
    {
        TilePlan plan = TilePlanner.Create(width, height, tile, padding);

        int lastX = plan.Tiles.Max(t => t.X);
        int lastY = plan.Tiles.Max(t => t.Y);
        Assert.Equal(width - plan.TileWidth, lastX);
        Assert.Equal(height - plan.TileHeight, lastY);

        // 最后一列/行的块必须存在（贴边块）。
        Assert.Contains(plan.Tiles, t => t.X == lastX);
        Assert.Contains(plan.Tiles, t => t.Y == lastY);
    }

    [Fact]
    public void SmallImage_UsesSingleTileWithReducedPadding()
    {
        TilePlan plan = TilePlanner.Create(100, 60, 256, 24);

        Assert.Single(plan.Tiles);
        Assert.Equal(100, plan.TileWidth);
        Assert.Equal(60, plan.TileHeight);
        Assert.Equal(24, plan.PaddingX);   // 100/4 = 25 > 24，保持 24
        Assert.Equal(15, plan.PaddingY);   // 60/4 = 15，按小图规则收缩
        TileRect tile = plan.Tiles[0];
        Assert.Equal(0, tile.X);
        Assert.Equal(0, tile.Y);
        Assert.Equal(100, tile.Width);
        Assert.Equal(60, tile.Height);
    }

    [Fact]
    public void SmallPadding_IsClampedToQuarterOfEdge()
    {
        TilePlan plan = TilePlanner.Create(8, 8, 256, 40);
        Assert.Single(plan.Tiles);
        Assert.Equal(2, plan.PaddingX);
        Assert.Equal(2, plan.PaddingY);
    }

    [Fact]
    public void PaddingTooLarge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(1000, 1000, 256, 128));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(0, 10, 256, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(10, 0, 256, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(10, 10, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(10, 10, 256, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TilePlanner.Create(10, 10, 5000, 8));
    }

    [Theory]
    [InlineData(ExecutionBackend.DirectML, "rrdb", 256)]
    [InlineData(ExecutionBackend.DirectML, "srvgg", 384)]
    [InlineData(ExecutionBackend.DirectML, "RRDB", 256)]
    [InlineData(ExecutionBackend.Cpu, "rrdb", 128)]
    [InlineData(ExecutionBackend.Cpu, "srvgg", 128)]
    public void AutoTileSize_FollowsSpec(ExecutionBackend backend, string arch, int expected)
    {
        Assert.Equal(expected, TilePlanner.ResolveAutoTileSize(backend, arch));
    }

    [Theory]
    [InlineData(ExecutionBackend.Cpu, "cugan", 512, 512, 512)]
    [InlineData(ExecutionBackend.Cpu, "rrdb", 512, 341, 512)]
    [InlineData(ExecutionBackend.Cpu, "cugan", 400, 300, 400)]
    [InlineData(ExecutionBackend.Cpu, "cugan", 1024, 768, 512)]
    [InlineData(ExecutionBackend.Cpu, "rrdb", 1600, 1067, 512)]
    [InlineData(ExecutionBackend.DirectML, "rrdb", 512, 512, 512)]
    [InlineData(ExecutionBackend.DirectML, "cugan", 1024, 768, 384)]
    public void AutoTileSize_WithDimensions_UsesMeasuredRules(
        ExecutionBackend backend,
        string arch,
        int width,
        int height,
        int expected)
    {
        Assert.Equal(expected, TilePlanner.ResolveAutoTileSize(backend, arch, width, height));
    }

    [Theory]
    [InlineData("rrdb")]
    [InlineData("cugan")]
    [InlineData("")]
    public void CompatibilityTileSize_IsStable256(string arch)
    {
        Assert.Equal(256, TilePlanner.ResolveCpuCompatibilityTileSize(arch));
    }

    [Theory]
    [InlineData(256, "rrdb", 24)]
    [InlineData(384, "srvgg", 32)]
    [InlineData(128, "srvgg", 16)]
    [InlineData(64, "other", 8)]
    public void AutoPadding_FollowsSpec(int tile, string arch, int expected)
    {
        Assert.Equal(expected, TilePlanner.ResolveAutoPadding(tile, arch));
    }

    /// <summary>一维覆盖：按起点排序后，第一块从 0 开始，相邻块有重叠，最后一块贴住右边缘。</summary>
    private static void AssertAxisCovers(IReadOnlyList<(int Start, int Length)> intervals, int dimension, int block)
    {
        var sorted = intervals.OrderBy(i => i.Start).ToList();
        Assert.Equal(0, sorted[0].Start);
        int covered = sorted[0].Start + sorted[0].Length;
        foreach ((int start, int length) in sorted.Skip(1))
        {
            Assert.True(start <= covered, $"存在间隙：start={start}, covered={covered}");
            covered = Math.Max(covered, start + length);
        }

        Assert.Equal(dimension, covered);
        Assert.True(sorted[^1].Start + block <= dimension);
        Assert.Equal(dimension - block, sorted[^1].Start);
    }
}
