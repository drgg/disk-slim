using DiskSlim.Core.Platform;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Rules;

/// <summary>规则命中的一个具体路径。</summary>
public sealed record RuleMatch(string Path, long TotalBytes, long ReclaimableBytes, long FileCount, long InaccessibleDirectories);

public sealed record RuleResult
{
    public required CleanupRule Rule { get; init; }

    public required IReadOnlyList<RuleMatch> Matches { get; init; }

    /// <summary>规则里列出的、当前正在运行的进程。</summary>
    public IReadOnlyList<string> RunningProcesses { get; init; } = [];

    public long TotalBytes => Matches.Sum(m => m.TotalBytes);

    public long ReclaimableBytes => Matches.Sum(m => m.ReclaimableBytes);

    public long FileCount => Matches.Sum(m => m.FileCount);

    public long InaccessibleDirectories => Matches.Sum(m => m.InaccessibleDirectories);
}

/// <summary>
/// 按顺序评估规则，统计每条规则能释放多少空间。只读取，不删除。
/// </summary>
/// <remarks>
/// 同一个路径只归属于第一条命中它的规则（见 <see cref="RuleMatcher"/>）；后面的规则如果命中了它的上级目录，
/// 会扣掉这部分，保证各项相加不会重复计算。因此规则文件里应该把具体的规则放在前面、宽泛的规则放在后面。
/// </remarks>
public sealed class RuleEvaluator
{
    private readonly string _driveRoot;
    private readonly IReadOnlySet<string> _runningProcesses;
    private readonly TimeProvider _time;
    private readonly RuleMatcher _matcher;
    private readonly Quarantine.QuarantineStore _quarantine;

    /// <param name="driveRoot">只统计位于这个磁盘上的路径，例如 <c>C:\</c>。</param>
    /// <param name="runningProcesses">当前运行的进程名；为 null 时不检查。</param>
    /// <param name="quarantine">这块盘上的隔离区；为 null 时使用默认位置。</param>
    public RuleEvaluator(string driveRoot, IReadOnlySet<string>? runningProcesses = null, TimeProvider? time = null, Quarantine.QuarantineStore? quarantine = null)
    {
        _driveRoot = driveRoot;
        _runningProcesses = runningProcesses ?? new HashSet<string>();
        _time = time ?? TimeProvider.System;
        _matcher = new RuleMatcher(driveRoot);
        _quarantine = quarantine ?? Quarantine.QuarantineStore.ForDrive(driveRoot);
    }

    public RuleResult Evaluate(CleanupRule rule, CancellationToken cancellationToken = default)
    {
        var cutoff = FileAge.Cutoff(rule.MinAgeDays, _time);
        var matches = new List<RuleMatch>();

        if (rule.Paths.Any(PathExpander.IsRecycleBin)
            && RecycleBin.Query(_driveRoot) is { } bin && (bin.Bytes > 0 || bin.ItemCount > 0))
        {
            matches.Add(new RuleMatch($"回收站（{_driveRoot}）", bin.Bytes, bin.Bytes, bin.ItemCount, 0));
        }

        if (rule.Paths.Any(PathExpander.IsQuarantine) && _quarantine.ListSessions() is { Count: > 0 } sessions)
        {
            // 总大小是整个隔离区；可释放的是超过 minAgeDays 天的批次。
            var expired = sessions.Where(s => cutoff is not { } c || s.CreatedAt < c).ToList();
            matches.Add(new RuleMatch(_quarantine.Root, sessions.Sum(s => s.TotalBytes), expired.Sum(s => s.TotalBytes), sessions.Sum(s => s.Items.Count), 0));
        }

        foreach (var target in _matcher.Match(rule))
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches.Add(Measure(target, cutoff, rule.Action is CleanupAction.Delete or CleanupAction.Recycle or CleanupAction.Quarantine, cancellationToken));
        }

        return new RuleResult { Rule = rule, Matches = matches, RunningProcesses = RunningOf(rule, _runningProcesses) };
    }

    /// <summary>规则列出的进程中正在运行的那些。</summary>
    public static IReadOnlyList<string> RunningOf(CleanupRule rule, IReadOnlySet<string> runningProcesses) => rule.Processes
        .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
        .Where(runningProcesses.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <param name="toolCleans">规则由工具清理时，只读和系统文件会被保留，不计为可释放。</param>
    private static RuleMatch Measure(MatchedTarget target, DateTimeOffset? cutoff, bool toolCleans, CancellationToken cancellationToken)
    {
        bool IsReclaimable(FileInfo file) =>
            FileAge.IsOlderThan(file, cutoff) && !(toolCleans && (file.Attributes & TreeScanner.ProtectedMask) != 0);

        if (target.IsFile)
        {
            var file = new FileInfo(target.Path);
            if (!file.Exists)
            {
                return new RuleMatch(target.Path, 0, 0, 0, 0);
            }

            return new RuleMatch(target.Path, file.Length, IsReclaimable(file) ? file.Length : 0, 1, 0);
        }

        var options = new ScanOptions { ReclaimableBefore = cutoff, ProtectedFilesAreNotReclaimable = toolCleans };
        var result = new TreeScanner(options).Scan(target.Path, cancellationToken);
        var root = result.Root;
        long total = root.TotalBytes, reclaimable = root.TotalReclaimableBytes, files = root.TotalFileCount;

        // 扣掉已经归属于前面规则的子路径。
        foreach (var excluded in target.Exclusions)
        {
            if (excluded.IsFile)
            {
                var file = new FileInfo(excluded.Path);
                if (file.Exists && (file.Attributes & TreeScanner.CloudOnlyMask) == 0)
                {
                    total -= file.Length;
                    files--;
                    if (IsReclaimable(file))
                    {
                        reclaimable -= file.Length;
                    }
                }
            }
            else if (root.FindDescendant(excluded.Path) is { } node)
            {
                total -= node.TotalBytes;
                reclaimable -= node.TotalReclaimableBytes;
                files -= node.TotalFileCount;
            }
        }

        return new RuleMatch(target.Path, Math.Max(0, total), Math.Max(0, reclaimable), Math.Max(0, files), result.Stats.InaccessibleDirectories);
    }
}
