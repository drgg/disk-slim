using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Visualization;

public enum TileKind
{
    /// <summary>一个子目录。</summary>
    Directory,

    /// <summary>直接位于父目录中的文件合计。</summary>
    OwnFiles,

    /// <summary>太小、放不下的项目合计。</summary>
    Other,
}

/// <summary>方块图中的一个方块。</summary>
/// <param name="Depth">1 是当前目录的直接子项，2 是嵌套在里面的下一层。</param>
/// <param name="ColorIndex">第一层方块的序号，同一个第一层方块里的子方块沿用它，用来区分颜色。</param>
/// <param name="Risk">这个方块（或它所在的上层方块）是某条规则的目标时，那条规则的风险等级。</param>
public sealed record TreemapTile(
    string Label,
    string Path,
    long Bytes,
    long CleanableBytes,
    TreemapRect Bounds,
    int Depth,
    TileKind Kind,
    DirNode? Node,
    RiskLevel? Risk,
    int ColorIndex,
    int ItemCount = 1);

/// <summary>把一个目录的内容排成两层嵌套的方块图。</summary>
public sealed class TreemapBuilder
{
    /// <summary>比这个面积（像素²）还小的项目合并成“其他”。</summary>
    public double MinTileArea { get; init; } = 36;

    /// <summary>第二层方块上方给第一层标签留的高度。</summary>
    public double HeaderHeight { get; init; } = 18;

    public double Padding { get; init; } = 2;

    /// <summary>第一层方块至少这么大才在里面画第二层。</summary>
    public double MinNestedWidth { get; init; } = 70;

    public double MinNestedHeight { get; init; } = 48;

    public int MaxTilesPerLevel { get; init; } = 400;

    public CleanableIndex Index { get; init; } = CleanableIndex.Empty;

    /// <summary>生成方块，父方块总是排在它的子方块前面。</summary>
    public IReadOnlyList<TreemapTile> Build(DirNode node, TreemapRect bounds, int depth = 2)
    {
        var tiles = new List<TreemapTile>();
        LayoutLevel(node, node.FullPath, bounds, level: 1, maxDepth: depth, colorIndex: 0, inheritedRisk: null, tiles);
        return tiles;
    }

    private void LayoutLevel(DirNode node, string nodePath, TreemapRect bounds, int level, int maxDepth, int colorIndex, RiskLevel? inheritedRisk, List<TreemapTile> tiles)
    {
        var items = node.Children
            .Where(c => c.TotalBytes > 0)
            .Select(c => new Item(c.Name, c.TotalBytes, TileKind.Directory, c))
            .ToList();
        if (node.OwnBytes > 0)
        {
            items.Add(new Item("（本目录中的文件）", node.OwnBytes, TileKind.OwnFiles, null));
        }

        var total = items.Sum(i => i.Bytes);
        if (total == 0 || bounds.Area <= 0)
        {
            return;
        }

        items.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        // 太小的项目合并成一个“其他”方块，既省得画一堆看不见的细线，也让剩下的方块比例正确。
        var minBytes = total * MinTileArea / bounds.Area;
        var visibleCount = 0;
        while (visibleCount < items.Count && visibleCount < MaxTilesPerLevel && items[visibleCount].Bytes >= minBytes)
        {
            visibleCount++;
        }

        var visible = items.Take(visibleCount).ToList();
        if (visibleCount < items.Count)
        {
            var rest = items.Skip(visibleCount).ToList();
            visible.Add(new Item($"其他 {rest.Count:N0} 项", rest.Sum(i => i.Bytes), TileKind.Other, null, rest.Count));
        }

        var rects = SquarifiedTreemap.Layout(visible.Select(i => (double)i.Bytes).ToArray(), bounds);
        for (var i = 0; i < visible.Count; i++)
        {
            var item = visible[i];
            var path = item.Kind == TileKind.Directory ? System.IO.Path.Join(nodePath, item.Label) : nodePath;
            var risk = inheritedRisk ?? (item.Kind == TileKind.Directory ? Index.TargetRisk(path) : null);
            var color = level == 1 ? i : colorIndex;
            var cleanable = item.Node is { } child ? Index.CleanableBytes(child) : 0;
            tiles.Add(new TreemapTile(item.Label, path, item.Bytes, cleanable, rects[i], level, item.Kind, item.Node, risk, color, item.Count));

            var rect = rects[i];
            if (level < maxDepth && item.Node is { } nested && rect.Width >= MinNestedWidth && rect.Height >= MinNestedHeight)
            {
                var inner = rect.Inset(Padding, HeaderHeight, Padding, Padding);
                LayoutLevel(nested, path, inner, level + 1, maxDepth, color, risk, tiles);
            }
        }
    }

    private sealed record Item(string Label, long Bytes, TileKind Kind, DirNode? Node, int Count = 1);
}
