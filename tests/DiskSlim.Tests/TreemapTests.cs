using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;
using DiskSlim.Core.Visualization;

namespace DiskSlim.Tests;

public sealed class TreemapTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(new double[] { 6, 6, 4, 3, 2, 2, 1 }, 600, 400)] // 论文中的例子
    [InlineData(new double[] { 100 }, 300, 200)]
    [InlineData(new double[] { 50, 30, 20, 0, 0 }, 200, 800)]
    [InlineData(new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, 1000, 50)]
    public void Tiles_fill_the_bounds_proportionally_without_overlapping(double[] sizes, double width, double height)
    {
        var bounds = new TreemapRect(10, 20, width, height);

        var rects = SquarifiedTreemap.Layout(sizes, bounds);

        var total = sizes.Sum();
        for (var i = 0; i < sizes.Length; i++)
        {
            var r = rects[i];
            Assert.Equal(bounds.Area * sizes[i] / total, r.Area, precision: 6);
            Assert.True(r.X >= bounds.X - 1e-6 && r.Y >= bounds.Y - 1e-6 && r.Right <= bounds.Right + 1e-6 && r.Bottom <= bounds.Bottom + 1e-6);
            for (var j = i + 1; j < sizes.Length; j++)
            {
                var o = rects[j];
                var overlapWidth = Math.Min(r.Right, o.Right) - Math.Max(r.X, o.X);
                var overlapHeight = Math.Min(r.Bottom, o.Bottom) - Math.Max(r.Y, o.Y);
                Assert.False(overlapWidth > 1e-6 && overlapHeight > 1e-6, $"方块 {i} 和 {j} 重叠");
            }
        }
    }

    [Fact]
    public void Squarified_layout_keeps_tiles_reasonably_square()
    {
        var rects = SquarifiedTreemap.Layout([6, 6, 4, 3, 2, 2, 1], new TreemapRect(0, 0, 6, 4));

        var worst = rects.Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
        Assert.True(worst < 3, $"最差长宽比 {worst:0.00}");
    }

    [Fact]
    public void Empty_or_zero_input_produces_empty_tiles()
    {
        Assert.Empty(SquarifiedTreemap.Layout([], new TreemapRect(0, 0, 100, 100)));
        Assert.All(SquarifiedTreemap.Layout([0, 0], new TreemapRect(0, 0, 100, 100)), r => Assert.Equal(0, r.Area));
        Assert.All(SquarifiedTreemap.Layout([5, 3], new TreemapRect(0, 0, 0, 100)), r => Assert.Equal(0, r.Area));
    }

    [Fact]
    public void Builder_nests_two_levels_and_puts_parents_first()
    {
        _dir.WriteFile("big/a/1.bin", 60_000);
        _dir.WriteFile("big/b/2.bin", 30_000);
        _dir.WriteFile("small/3.bin", 10_000);
        _dir.WriteFile("root.bin", 5_000);
        var root = new TreeScanner().Scan(_dir.Root).Root;

        var tiles = new TreemapBuilder().Build(root, new TreemapRect(0, 0, 800, 600));

        var top = tiles.Where(t => t.Depth == 1).ToList();
        Assert.Equal(["big", "small", "（本目录中的文件）"], top.Select(t => t.Label));
        Assert.Equal(TileKind.OwnFiles, top[2].Kind);
        var nested = tiles.Where(t => t.Depth == 2 && t.Path.StartsWith(_dir.PathOf("big"), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["a", "b"], nested.Select(t => t.Label));
        Assert.True(tiles.ToList().IndexOf(top[0]) < tiles.ToList().IndexOf(nested[0]));
        Assert.All(nested, t => Assert.Equal(top[0].ColorIndex, t.ColorIndex));
    }

    [Fact]
    public void Builder_merges_tiny_items_into_other()
    {
        _dir.WriteFile("huge/x.bin", 1_000_000);
        for (var i = 0; i < 20; i++)
        {
            _dir.WriteFile($"tiny{i}/x.bin", 10);
        }

        var root = new TreeScanner().Scan(_dir.Root).Root;

        var tiles = new TreemapBuilder().Build(root, new TreemapRect(0, 0, 400, 300), depth: 1);

        var other = Assert.Single(tiles, t => t.Kind == TileKind.Other);
        Assert.Equal(20, other.ItemCount);
        Assert.Equal(200, other.Bytes);
    }

    [Fact]
    public void Cleanable_index_marks_targets_and_sums_cleanable_bytes_upwards()
    {
        _dir.WriteFile("app/cache/c.bin", 700);
        _dir.WriteFile("app/data/d.bin", 300);
        _dir.WriteFile("chat/history.db", 500);
        var root = new TreeScanner().Scan(_dir.Root).Root;
        var evaluator = new RuleEvaluator(Path.GetPathRoot(_dir.Root)!);
        var results = new[]
        {
            evaluator.Evaluate(new CleanupRule { Id = "cache", Name = "缓存", Risk = RiskLevel.Low, Paths = [_dir.PathOf("app/cache")] }),
            evaluator.Evaluate(new CleanupRule { Id = "chat", Name = "聊天", Risk = RiskLevel.High, Paths = [_dir.PathOf("chat")] }),
        };

        var index = CleanableIndex.Build(root, results);

        Assert.Equal(RiskLevel.Low, index.TargetRisk(_dir.PathOf("app/cache")));
        Assert.Equal(RiskLevel.High, index.TargetRisk(_dir.PathOf("chat")));
        Assert.Null(index.TargetRisk(_dir.PathOf("app")));
        Assert.Equal(700, index.CleanableBytes(root.FindDescendant(_dir.PathOf("app"))!));
        Assert.Equal(700, index.CleanableBytes(root));                                      // “仅供参考”的不算可清理
        Assert.Equal(0, index.CleanableBytes(root.FindDescendant(_dir.PathOf("chat"))!));

        var tiles = new TreemapBuilder { Index = index }.Build(root, new TreemapRect(0, 0, 800, 600));
        Assert.Equal(RiskLevel.Low, tiles.Single(t => t.Label == "cache").Risk);
        Assert.Equal(700, tiles.Single(t => t.Label == "app").CleanableBytes);
    }
}
