using System.Reflection;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Reporting;

public sealed record ReportOptions
{
    /// <summary>要分析的磁盘根目录，例如 <c>C:\</c>。</summary>
    public required string DriveRoot { get; init; }

    /// <summary>空间分布从哪个目录开始扫描，默认是整个磁盘。</summary>
    public string? MapRoot { get; init; }

    public int TopCount { get; init; } = 15;

    public RuleSet? Rules { get; init; }

    public bool IncludeRules { get; init; } = true;

    public bool IncludeSpaceMap { get; init; } = true;
}

/// <summary>生成完整的只读扫描报告：磁盘概况、规则检查、空间分布。</summary>
public sealed class DiskReportBuilder
{
    private volatile string _stage = "准备中";
    private volatile TreeScanner? _mapScanner;

    /// <summary>当前阶段的文字描述，可从其他线程读取以显示进度。</summary>
    public string Stage => _stage;

    /// <summary>正在进行的空间分布扫描；不在扫描时为 null。</summary>
    public TreeScanner? MapScanner => _mapScanner;

    /// <summary>最近一次空间分布扫描的完整目录树（报告里只有摘要），供图形界面逐层浏览。</summary>
    public ScanResult? MapResult { get; private set; }

    public static string ToolVersion =>
        typeof(DiskReportBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public ScanReport Build(ReportOptions options, CancellationToken cancellationToken = default)
    {
        var generatedAt = DateTimeOffset.Now;
        var isElevated = SystemInfo.IsElevated();
        var drive = DescribeDrive(options.DriveRoot);

        var ruleResults = new List<RuleResult>();
        var ruleCount = 0;
        var rulesChecked = options.IncludeRules && options.Rules is not null;
        if (rulesChecked)
        {
            var rules = options.Rules!.Rules;
            ruleCount = rules.Count;
            var evaluator = new RuleEvaluator(drive.Root, SystemInfo.RunningProcessNames());
            for (var i = 0; i < rules.Count; i++)
            {
                _stage = $"检查规则 {i + 1}/{rules.Count}：{rules[i].Name}";
                var result = evaluator.Evaluate(rules[i], cancellationToken);
                if (result.TotalBytes > 0 || result.InaccessibleDirectories > 0)
                {
                    ruleResults.Add(result);
                }
            }
        }

        SpaceMap? map = null;
        if (options.IncludeSpaceMap)
        {
            var mapRoot = Path.GetFullPath(options.MapRoot ?? drive.Root);
            _stage = $"扫描 {mapRoot} 的空间分布";
            var scanner = new TreeScanner(new ScanOptions { LargestFileCount = options.TopCount });
            _mapScanner = scanner;
            try
            {
                MapResult = scanner.Scan(mapRoot, cancellationToken);
                map = BuildSpaceMap(MapResult, options.TopCount);
            }
            finally
            {
                _mapScanner = null;
            }
        }

        _stage = "完成";
        return new ScanReport
        {
            ToolVersion = ToolVersion,
            GeneratedAt = generatedAt,
            IsElevated = isElevated,
            Drive = drive,
            RulesChecked = rulesChecked,
            RuleCount = ruleCount,
            Rules = ruleResults,
            SpaceMap = map,
            Notes = BuildNotes(drive, map, isElevated),
        };
    }

    private static DriveSummary DescribeDrive(string driveRoot)
    {
        var info = new DriveInfo(driveRoot);
        string label;
        try
        {
            label = info.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            label = "";
        }

        return new DriveSummary(info.RootDirectory.FullName, label, info.DriveFormat, info.TotalSize, info.TotalFreeSpace);
    }

    private static SpaceMap BuildSpaceMap(ScanResult result, int top)
    {
        var root = result.Root;
        var focus = new List<(string Title, string Path)>
        {
            ("扫描根目录", root.FullPath),
            ("用户目录", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ("AppData\\Local", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("AppData\\Roaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        };

        var views = new List<FolderView>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, path) in focus)
        {
            if (string.IsNullOrEmpty(path) || root.FindDescendant(path) is not { } node || !seen.Add(node.FullPath))
            {
                continue;
            }

            views.Add(DescribeFolder(title, node, top));
        }

        return new SpaceMap
        {
            Root = root.FullPath,
            Folders = views,
            LargestFiles = result.LargestFiles,
            Stats = result.Stats,
            ElapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1),
            Errors = result.Errors,
        };
    }

    internal static FolderView DescribeFolder(string title, DirNode node, int top)
    {
        var path = node.FullPath;
        var items = node.Children
            .Where(c => c.TotalBytes > 0)
            .Select(c => new FolderItem(c.Name, Path.Join(path, c.Name), c.TotalBytes, c.TotalFileCount, IsOwnFiles: false, c.TotalInaccessibleCount > 0))
            .ToList();
        if (node.OwnBytes > 0)
        {
            items.Add(new FolderItem("（本目录中的文件）", path, node.OwnBytes, node.OwnFileCount, IsOwnFiles: true, Incomplete: false));
        }

        var sorted = items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
        return new FolderView(title, path, node.TotalBytes, sorted);
    }

    private static List<string> BuildNotes(DriveSummary drive, SpaceMap? map, bool isElevated)
    {
        var notes = new List<string> { "本工具只读取文件信息，不会修改或删除任何文件。" };

        if (!isElevated)
        {
            notes.Add("当前没有以管理员身份运行：部分系统目录（如 System Volume Information、Windows\\Temp）无法读取，这些位置的统计会偏小。");
        }

        if (map is not null)
        {
            if (string.Equals(map.Root, drive.Root, StringComparison.OrdinalIgnoreCase))
            {
                var gap = drive.UsedBytes - map.Stats.Bytes;
                if (gap > 0)
                {
                    notes.Add($"磁盘已用 {ByteSize.Format(drive.UsedBytes)}，扫描到的文件合计 {ByteSize.Format(map.Stats.Bytes)}，"
                        + $"约 {ByteSize.Format(gap)} 没有统计到：通常是系统还原点、无权限读取的目录和 NTFS 自身的元数据。");
                }
                else
                {
                    notes.Add($"扫描到的文件合计 {ByteSize.Format(map.Stats.Bytes)}，比磁盘已用空间多 {ByteSize.Format(-gap)}：主要是硬链接被重复计算（例如 Windows\\WinSxS）。");
                }
            }

            var cloudItems = map.Stats.CloudOnlyFiles + map.Stats.CloudOnlyDirectories;
            if (cloudItems > 0)
            {
                notes.Add($"有 {cloudItems:N0} 个 OneDrive 等云盘的“仅在线”文件或文件夹，它们不占本地空间，没有计入，也没有触发下载。");
            }
        }

        notes.Add("大小按文件长度统计，没有扣除 NTFS 压缩；硬链接可能被重复计算，因此 Windows 目录的数值会偏大。");
        return notes;
    }
}
