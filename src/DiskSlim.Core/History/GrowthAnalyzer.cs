namespace DiskSlim.Core.History;

/// <summary>
/// 一个变化明显的位置。
/// </summary>
/// <param name="Delta">归到这一项的变化量（增长为正，减少为负）。</param>
/// <param name="IsRemainder">
/// 为 true 表示“这个文件夹里除了已经单独列出的子文件夹以外的变化”，此时 <paramref name="Delta"/> 只是其中一部分，
/// 整个文件夹的变化是 <paramref name="FolderDelta"/>。
/// </param>
/// <param name="OldBytes">为 null 表示以前的快照里没有单独记录（小于记录门槛，或者还不存在）。</param>
/// <param name="NewBytes">为 null 表示现在小于记录门槛，或者已经被删除。</param>
public sealed record GrowthItem(string Path, long Delta, bool IsRemainder, long FolderDelta, long? OldBytes, long? NewBytes);

public sealed record RuleChange(string Id, string Name, long OldBytes, long NewBytes)
{
    public long Delta => NewBytes - OldBytes;
}

public sealed record GrowthReport
{
    public required SpaceSnapshot From { get; init; }

    public required SpaceSnapshot To { get; init; }

    public required long MinBytes { get; init; }

    /// <summary>增长最多的位置，从大到小。各项互不重叠，相加不会重复计算。</summary>
    public required IReadOnlyList<GrowthItem> Grown { get; init; }

    /// <summary>减少最多的位置，从多到少（<see cref="GrowthItem.Delta"/> 为负）。</summary>
    public required IReadOnlyList<GrowthItem> Shrunk { get; init; }

    /// <summary>规则项目的变化；有一份快照没有检查规则时为 null。</summary>
    public IReadOnlyList<RuleChange>? Rules { get; init; }

    public long UsedDelta => To.UsedBytes - From.UsedBytes;

    public long ScannedDelta => To.ScannedBytes - From.ScannedBytes;

    public TimeSpan Span => To.Time - From.Time;

    /// <summary>两次扫描的权限不同：系统目录的变化可能只是能不能读到的区别。</summary>
    public bool ElevationDiffers => From.IsElevated != To.IsElevated;
}

/// <summary>比较两份快照，找出空间主要涨在哪里、减在哪里。</summary>
public static class GrowthAnalyzer
{
    public const long DefaultMinBytes = 100L << 20;
    public const long DefaultRuleMinBytes = 10L << 20;

    /// <summary>
    /// 从一组快照里挑出比较基准：<paramref name="latest"/> 之前、离“<paramref name="age"/> 以前”最近且不晚于它的那份；
    /// 记录都没有那么早时，用最早的一份。没有更早的快照时返回 null。
    /// </summary>
    public static SnapshotSummary? PickBaseline(IEnumerable<SnapshotSummary> history, DateTimeOffset latest, TimeSpan age)
    {
        var candidates = history.Where(h => h.HasDetails && h.Time < latest).OrderBy(h => h.Time).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = latest - age;
        return candidates.LastOrDefault(c => c.Time <= target) ?? candidates[0];
    }

    public static GrowthReport Compare(SpaceSnapshot from, SpaceSnapshot to, long minBytes = DefaultMinBytes, int top = 15, long ruleMinBytes = DefaultRuleMinBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minBytes, 1);
        if (!string.Equals(from.Drive, to.Drive, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"不能比较不同磁盘的快照：{from.Drive} 和 {to.Drive}");
        }

        var root = new Node(to.Drive, null);
        Merge(root, from.Directories, isOld: true);
        Merge(root, to.Directories, isOld: false);

        var grown = new List<GrowthItem>();
        var shrunk = new List<GrowthItem>();
        Collect(root, sign: 1, minBytes, grown);
        Collect(root, sign: -1, minBytes, shrunk);

        return new GrowthReport
        {
            From = from,
            To = to,
            MinBytes = minBytes,
            Grown = grown.OrderByDescending(g => g.Delta).ThenBy(g => g.Path, StringComparer.OrdinalIgnoreCase).Take(top).ToList(),
            Shrunk = shrunk.OrderBy(g => g.Delta).ThenBy(g => g.Path, StringComparer.OrdinalIgnoreCase).Take(top).ToList(),
            Rules = CompareRules(from, to, ruleMinBytes),
        };
    }

    private static void Merge(Node root, IReadOnlyList<SnapshotDirectory> directories, bool isOld)
    {
        var nodes = new Node?[directories.Count];
        for (var i = 0; i < directories.Count; i++)
        {
            var d = directories[i];
            Node node;
            if (d.Parent < 0)
            {
                node = root;
            }
            else if (d.Parent < i && nodes[d.Parent] is { } parent)
            {
                node = parent.Child(d.Name);
            }
            else
            {
                continue; // 损坏的快照：上级不在前面，忽略这一项
            }

            nodes[i] = node;
            if (isOld)
            {
                node.Old = d.Bytes;
            }
            else
            {
                node.New = d.Bytes;
            }
        }
    }

    /// <summary>
    /// 自底向上找变化集中的位置：子文件夹里变化够大的先单独列出；剩下没被子文件夹解释的部分也够大时，再把这个文件夹列出来。
    /// 返回这棵子树里已经列出的变化总量。增长和减少各算一遍（<paramref name="sign"/>），互不抵消。
    /// </summary>
    private static long Collect(Node node, int sign, long minBytes, List<GrowthItem> output)
    {
        long explained = 0;
        if (node.Children is { } children)
        {
            foreach (var child in children.Values)
            {
                explained += Collect(child, sign, minBytes, output);
            }
        }

        var delta = sign * ((node.New ?? 0) - (node.Old ?? 0));
        var residual = delta - explained;
        if (residual >= minBytes)
        {
            output.Add(new GrowthItem(node.FullPath, sign * residual, IsRemainder: explained > 0, sign * delta, node.Old, node.New));
            explained += residual;
        }

        return explained;
    }

    private static List<RuleChange>? CompareRules(SpaceSnapshot from, SpaceSnapshot to, long minBytes)
    {
        if (from.Rules is null || to.Rules is null)
        {
            return null;
        }

        var old = from.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var now = to.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return old.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                old.TryGetValue(id, out var o);
                now.TryGetValue(id, out var n);
                return new RuleChange(id, n?.Name ?? o!.Name, o?.Bytes ?? 0, n?.Bytes ?? 0);
            })
            .Where(c => Math.Abs(c.Delta) >= minBytes)
            .OrderByDescending(c => Math.Abs(c.Delta))
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class Node(string name, Node? parent)
    {
        public Dictionary<string, Node>? Children { get; private set; }

        public long? Old { get; set; }

        public long? New { get; set; }

        public string FullPath => parent is null ? name : Path.Join(parent.FullPath, name);

        public Node Child(string childName)
        {
            Children ??= new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            if (!Children.TryGetValue(childName, out var child))
            {
                child = new Node(childName, this);
                Children.Add(childName, child);
            }

            return child;
        }
    }
}
