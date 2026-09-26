using System.Text.Json.Serialization;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.History;

/// <summary>快照中的一个目录。<see cref="Parent"/> 是上级目录在列表中的下标，根目录为 -1。</summary>
public sealed record SnapshotDirectory(int Parent, string Name, long Bytes, long Files);

public sealed record SnapshotRule(string Id, string Name, long Bytes);

/// <summary>
/// 一次完整扫描的精简记录：磁盘用量、各规则的大小，以及不小于 <see cref="MinDirectoryBytes"/> 的目录的大小。
/// 更小的目录不单独记录，大小计入上级目录：一块装满软件的系统盘约 7000 个目录，压缩后约 100 KB。
/// </summary>
/// <remarks>
/// 因为小目录没有记录，比较时一个目录“以前没有记录”只能当作 0，误差不超过 <see cref="MinDirectoryBytes"/>，
/// 远小于报告变化的门槛（<see cref="GrowthAnalyzer.DefaultMinBytes"/>）。
/// </remarks>
public sealed record SpaceSnapshot
{
    public const long DefaultMinDirectoryBytes = 4L << 20;

    public int Version { get; init; } = 1;

    public required DateTimeOffset Time { get; init; }

    /// <summary>磁盘根目录，例如 <c>C:\</c>。</summary>
    public required string Drive { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>以管理员身份扫描时能读到更多系统目录，和普通权限的快照比较时要注意。</summary>
    public bool IsElevated { get; init; }

    public long MinDirectoryBytes { get; init; }

    /// <summary>按先序排列：上级目录总在下级目录前面，第一项是根目录。</summary>
    public IReadOnlyList<SnapshotDirectory> Directories { get; init => field = value ?? []; } = [];

    /// <summary>各规则命中的大小；为 null 表示这次扫描没有检查规则。</summary>
    public IReadOnlyList<SnapshotRule>? Rules { get; init; }

    [JsonIgnore]
    public long UsedBytes => TotalBytes - FreeBytes;

    [JsonIgnore]
    public long ScannedBytes => Directories.Count > 0 ? Directories[0].Bytes : 0;

    /// <summary>从一次完整扫描生成快照。只有扫描整块磁盘时才能生成，否则和其他快照无法比较。</summary>
    public static SpaceSnapshot Create(ScanReport report, ScanResult map, long minDirectoryBytes = DefaultMinDirectoryBytes)
    {
        if (!string.Equals(Path.GetFullPath(map.Root.FullPath), Path.GetFullPath(report.Drive.Root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只有扫描整块磁盘时才能生成空间快照。", nameof(map));
        }

        var directories = new List<SnapshotDirectory>();
        var stack = new Stack<(DirNode Node, int Parent)>();
        stack.Push((map.Root, -1));
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var index = directories.Count;
            directories.Add(new SnapshotDirectory(parent, node.Name, node.TotalBytes, node.TotalFileCount));

            // 倒序入栈，出栈时就是原来的顺序；子目录一定不比上级大，所以小目录下面不用再看。
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                if (node.Children[i].TotalBytes >= minDirectoryBytes)
                {
                    stack.Push((node.Children[i], index));
                }
            }
        }

        return new SpaceSnapshot
        {
            Time = report.GeneratedAt,
            Drive = report.Drive.Root,
            TotalBytes = report.Drive.TotalBytes,
            FreeBytes = report.Drive.FreeBytes,
            IsElevated = report.IsElevated,
            MinDirectoryBytes = minDirectoryBytes,
            Directories = directories,
            Rules = report.RulesChecked ? report.Rules.Select(r => new SnapshotRule(r.Rule.Id, r.Rule.Name, r.TotalBytes)).ToList() : null,
        };
    }
}

/// <summary>历史索引中的一行：只有磁盘用量，画趋势图时不用打开每份快照。</summary>
public sealed record SnapshotSummary
{
    public required DateTimeOffset Time { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long ScannedBytes { get; init; }

    public bool IsElevated { get; init; }

    /// <summary>快照文件名；为 null 表示明细已经按保留策略删除，只剩用量数字。</summary>
    public string? File { get; init; }

    [JsonIgnore]
    public long UsedBytes => TotalBytes - FreeBytes;

    [JsonIgnore]
    public bool HasDetails => File is not null;

    public static SnapshotSummary Of(SpaceSnapshot snapshot, string? file) => new()
    {
        Time = snapshot.Time,
        TotalBytes = snapshot.TotalBytes,
        FreeBytes = snapshot.FreeBytes,
        ScannedBytes = snapshot.ScannedBytes,
        IsElevated = snapshot.IsElevated,
        File = file,
    };
}
