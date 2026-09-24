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
/// 同一个路径只归属于第一条命中它的规则；后面的规则如果命中了它的上级目录，会扣掉这部分，保证各项相加不会重复计算。
/// 因此规则文件里应该把具体的规则放在前面、宽泛的规则放在后面。
/// </remarks>
public sealed class RuleEvaluator
{
    private readonly string _driveRoot;
    private readonly IReadOnlySet<string> _runningProcesses;
    private readonly TimeProvider _time;
    private readonly List<Claim> _claims = [];

    /// <param name="driveRoot">只统计位于这个磁盘上的路径，例如 <c>C:\</c>。</param>
    /// <param name="runningProcesses">当前运行的进程名；为 null 时不检查。</param>
    public RuleEvaluator(string driveRoot, IReadOnlySet<string>? runningProcesses = null, TimeProvider? time = null)
    {
        _driveRoot = driveRoot;
        _runningProcesses = runningProcesses ?? new HashSet<string>();
        _time = time ?? TimeProvider.System;
    }

    public RuleResult Evaluate(CleanupRule rule, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? cutoff = rule.MinAgeDays is > 0 and var days
            ? _time.GetUtcNow() - TimeSpan.FromDays(days)
            : null;

        var matches = new List<RuleMatch>();
        foreach (var pattern in rule.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathExpander.IsRecycleBin(pattern))
            {
                if (RecycleBin.Query(_driveRoot) is { } bin && (bin.Bytes > 0 || bin.ItemCount > 0))
                {
                    matches.Add(new RuleMatch($"回收站（{_driveRoot}）", bin.Bytes, bin.Bytes, bin.ItemCount, 0));
                }

                continue;
            }

            foreach (var path in PathExpander.Expand(pattern))
            {
                if (!PathExpander.IsSameOrInside(path, _driveRoot) || IsClaimed(path))
                {
                    continue;
                }

                matches.Add(Measure(path, cutoff, cancellationToken));
            }
        }

        var running = rule.Processes
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
            .Where(_runningProcesses.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RuleResult { Rule = rule, Matches = matches, RunningProcesses = running };
    }

    private bool IsClaimed(string path) => _claims.Any(c => PathExpander.IsSameOrInside(path, c.Path));

    private RuleMatch Measure(string path, DateTimeOffset? cutoff, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            var length = file.Length;
            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            _claims.Add(new Claim(path, IsFile: true, length, lastWrite));
            return new RuleMatch(path, length, IsReclaimable(lastWrite, cutoff) ? length : 0, 1, 0);
        }

        var result = new TreeScanner(new ScanOptions { ReclaimableBefore = cutoff }).Scan(path, cancellationToken);
        var root = result.Root;
        long total = root.TotalBytes, reclaimable = root.TotalReclaimableBytes, files = root.TotalFileCount;

        // 扣掉已经归属于前面规则的子路径。只扣最外层的：内层的已经包含在外层目录的扫描结果里。
        var inside = _claims.Where(c => PathExpander.IsSameOrInside(c.Path, path)).ToList();
        var outermost = inside.Where(c => !inside.Any(o => !o.IsFile && o != c && PathExpander.IsSameOrInside(c.Path, o.Path)));
        foreach (var claim in outermost)
        {
            if (claim.IsFile)
            {
                total -= claim.Bytes;
                files--;
                if (IsReclaimable(claim.LastWriteTimeUtc, cutoff))
                {
                    reclaimable -= claim.Bytes;
                }
            }
            else if (root.FindDescendant(claim.Path) is { } node)
            {
                total -= node.TotalBytes;
                reclaimable -= node.TotalReclaimableBytes;
                files -= node.TotalFileCount;
            }
        }

        _claims.Add(new Claim(path, IsFile: false, 0, default));
        return new RuleMatch(path, total, reclaimable, files, result.Stats.InaccessibleDirectories);
    }

    private static bool IsReclaimable(DateTimeOffset lastWriteUtc, DateTimeOffset? cutoff) =>
        cutoff is not { } c || lastWriteUtc < c;

    private sealed record Claim(string Path, bool IsFile, long Bytes, DateTimeOffset LastWriteTimeUtc);
}
