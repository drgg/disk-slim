using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Cleaning;

public sealed record CleanupFailure(string Path, string Reason);

/// <summary>一条规则的清理结果（模拟运行时是“将会”的结果）。</summary>
public sealed class RuleOutcome
{
    internal const int MaxRecordedFailures = 50;
    private readonly List<CleanupFailure> _failures = [];

    internal RuleOutcome(PlannedRule planned) => Planned = planned;

    public PlannedRule Planned { get; }

    public CleanupRule Rule => Planned.Rule;

    /// <summary>删除（或移到回收站、或从回收站清空）的文件数。</summary>
    public long Files { get; internal set; }

    public long Bytes { get; internal set; }

    /// <summary>删除的空目录数。</summary>
    public long Directories { get; internal set; }

    /// <summary>因为太新（在 minAgeDays 之内）而保留的文件。</summary>
    public long SkippedTooNew { get; internal set; }

    /// <summary>因为只读或系统属性而保留的文件。</summary>
    public long SkippedProtected { get; internal set; }

    /// <summary>跳过的链接和云盘仅在线项目。</summary>
    public long SkippedLinks { get; internal set; }

    /// <summary>删除失败的文件（通常是正在被使用），以及无法读取的目录。</summary>
    public long Failed { get; internal set; }

    public long FailedBytes { get; internal set; }

    /// <summary>失败明细（最多记录若干条）。</summary>
    public IReadOnlyList<CleanupFailure> Failures => _failures;

    /// <summary>执行被取消，这条规则没有做完（或没有开始）。</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>移入隔离区的文件在清理结束后立即被彻底删除了（不能再撤销）。</summary>
    public bool PurgedAfterQuarantine { get; internal set; }

    public long SkippedTotal => SkippedTooNew + SkippedProtected + SkippedLinks;

    internal void AddFailure(string path, long bytes, string reason)
    {
        Failed++;
        FailedBytes += bytes;
        if (_failures.Count < MaxRecordedFailures)
        {
            _failures.Add(new CleanupFailure(path, reason));
        }
    }
}

public sealed class CleanupResult
{
    public required bool DryRun { get; init; }

    public required IReadOnlyList<RuleOutcome> Rules { get; init; }

    public required long FreeBytesBefore { get; init; }

    public required long FreeBytesAfter { get; init; }

    /// <summary>磁盘总容量（用来判断剩余空间是否紧张）；读不到时为 0。</summary>
    public long DriveTotalBytes { get; init; }

    public bool Cancelled { get; init; }

    public string? LogPath { get; init; }

    /// <summary>这次移入隔离区的批次 id；没有移入任何文件，或者已经立即彻底删除时为 null。</summary>
    public string? QuarantineSessionId { get; init; }

    /// <summary>这次移入隔离区的文件在清理后立即被彻底删除了，空间已经释放。</summary>
    public bool QuarantinePurged { get; init; }

    /// <summary>要求立即彻底删除、但删除失败时的原因（文件仍在隔离区里，可以稍后再删）。</summary>
    public string? QuarantinePurgeError { get; init; }

    /// <summary>永久删除的字节数（不含移到回收站和移入隔离区的）。</summary>
    public long DeletedBytes => Where(IsPermanent).Sum(r => r.Bytes);

    public long DeletedFiles => Where(IsPermanent).Sum(r => r.Files);

    /// <summary>移到回收站的字节数：暂时还占着空间，清空回收站后才释放。</summary>
    public long RecycledBytes => Where(CleanupAction.Recycle).Sum(r => r.Bytes);

    public long RecycledFiles => Where(CleanupAction.Recycle).Sum(r => r.Files);

    /// <summary>移入隔离区的字节数：可以撤销，彻底删除后才释放空间。</summary>
    public long QuarantinedBytes => Where(CleanupAction.Quarantine).Sum(r => r.Bytes);

    public long QuarantinedFiles => Where(CleanupAction.Quarantine).Sum(r => r.Files);

    public long TotalFiles => Rules.Sum(r => r.Files);

    private static bool IsPermanent(CleanupAction action) => action is not (CleanupAction.Recycle or CleanupAction.Quarantine);

    private IEnumerable<RuleOutcome> Where(CleanupAction action) => Rules.Where(r => r.Rule.Action == action);

    private IEnumerable<RuleOutcome> Where(Func<CleanupAction, bool> predicate) => Rules.Where(r => predicate(r.Rule.Action));
}

/// <summary>
/// 按计划清理。<paramref name="dryRun"/> 为 true 时走完全相同的判断流程，只是不执行任何写操作，
/// 所以模拟运行的结果就是真实执行时会做的事（正在使用的文件除外，只有真正删除时才知道）。
/// </summary>
/// <remarks>
/// 每个文件都要满足全部条件才会被处理：
/// <list type="bullet">
/// <item>不属于其他规则（<see cref="MatchedTarget.Exclusions"/>）；</item>
/// <item>不是链接，也不是云盘仅在线文件——链接和目录联接既不跟进也不删除；</item>
/// <item>没有只读或系统属性；</item>
/// <item>比规则的 minAgeDays 更旧（按修改时间和创建时间中较晚的算）。</item>
/// </list>
/// 规则指定的目录本身永远保留；其中清空了的子目录会被删除（delete 和 quarantine 方式）。
/// </remarks>
/// <param name="quarantine">隔离区；为 null 时使用计划所在磁盘的默认隔离区。</param>
public sealed class CleanupExecutor(IFileOperations? operations = null, IOperationLog? log = null, TimeProvider? time = null, Quarantine.QuarantineStore? quarantine = null)
{
    private const int MaxDepth = 256;

    private static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly IFileOperations _ops = operations ?? new WindowsFileOperations();
    private readonly IOperationLog _log = log ?? NullOperationLog.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile string _currentRule = "";
    private long _filesVisited;
    private long _bytesCleaned;
    private Quarantine.QuarantineStore? _quarantineStore = quarantine;
    private Quarantine.QuarantineWriter? _quarantineWriter;

    /// <summary>当前正在处理的规则名，可从其他线程读取以显示进度。</summary>
    public string CurrentRule => _currentRule;

    public long FilesVisited => Interlocked.Read(ref _filesVisited);

    public long BytesCleaned => Interlocked.Read(ref _bytesCleaned);

    /// <param name="purgeQuarantine">
    /// 清理结束后立即彻底删除这次移入隔离区的文件，马上释放空间（不能再撤销）。用于剩余空间紧张、等不了 7 天的时候。
    /// 取消了的清理不会删除，已经移入的文件留在隔离区里。
    /// </param>
    public CleanupResult Execute(CleanupPlan plan, bool dryRun, CancellationToken cancellationToken = default, bool purgeQuarantine = false)
    {
        _quarantineStore ??= Quarantine.QuarantineStore.ForDrive(plan.DriveRoot);
        _quarantineWriter = null;
        var (freeBefore, total) = Platform.DiskSpace.Query(plan.DriveRoot);
        if (!dryRun)
        {
            Log("run-start", detail: $"DiskSlim {Reporting.DiskReportBuilder.ToolVersion}，磁盘 {plan.DriveRoot}，剩余 {freeBefore} 字节，{plan.Rules.Count} 条规则");
        }

        var outcomes = new List<RuleOutcome>();
        var cancelled = false;
        foreach (var planned in plan.Rules)
        {
            var outcome = new RuleOutcome(planned);
            outcomes.Add(outcome);
            if (planned.Status != PlanStatus.Ready)
            {
                continue;
            }

            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                outcome.Cancelled = true;
                continue;
            }

            _currentRule = planned.Rule.Name;
            if (!dryRun)
            {
                Log("rule-start", planned.Rule.Id, detail: KebabCaseEnumConverter<CleanupAction>.ToKebabCase(planned.Rule.Action.ToString()));
            }

            try
            {
                RunRule(plan, planned, outcome, dryRun, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                outcome.Cancelled = true;
                cancelled = true;
            }

            if (!dryRun)
            {
                Log("rule-end", planned.Rule.Id, bytes: outcome.Bytes,
                    detail: $"文件 {outcome.Files}，空目录 {outcome.Directories}，跳过 {outcome.SkippedTotal}，失败 {outcome.Failed}{(outcome.Cancelled ? "，已取消" : "")}");
            }
        }

        var sessionId = _quarantineWriter is { Count: > 0 } writer ? writer.Id : null;
        var purged = false;
        string? purgeError = null;
        if (!dryRun && purgeQuarantine && !cancelled && sessionId is not null)
        {
            (purged, purgeError) = PurgeSession(sessionId);
            if (purged)
            {
                foreach (var outcome in outcomes.Where(o => o.Rule.Action == CleanupAction.Quarantine))
                {
                    outcome.PurgedAfterQuarantine = true;
                }

                sessionId = null;
            }
        }

        var freeAfter = dryRun ? freeBefore : FreeSpace(plan.DriveRoot);
        if (!dryRun)
        {
            Log("run-end", bytes: freeAfter - freeBefore, detail: $"剩余 {freeBefore} → {freeAfter} 字节{(cancelled ? "，已取消" : "")}");
        }

        return new CleanupResult
        {
            DryRun = dryRun,
            Rules = outcomes,
            FreeBytesBefore = freeBefore,
            FreeBytesAfter = freeAfter,
            DriveTotalBytes = total,
            Cancelled = cancelled,
            LogPath = dryRun ? null : _log.FilePath,
            QuarantineSessionId = sessionId,
            QuarantinePurged = purged,
            QuarantinePurgeError = purgeError,
        };
    }

    /// <summary>彻底删除这次移入隔离区的一批，返回是否成功和失败原因。</summary>
    private (bool Purged, string? Error) PurgeSession(string sessionId)
    {
        if (_quarantineStore!.Find(sessionId) is not { } session)
        {
            return (false, "隔离区里找不到这一批");
        }

        try
        {
            _quarantineStore.Purge(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("failed", path: session.Directory, bytes: session.TotalBytes, detail: "立即彻底删除失败：" + Reason(ex));
            return (false, Reason(ex));
        }

        Log("purged-quarantine", path: session.Directory, bytes: session.TotalBytes, detail: $"{session.Items.Count} 个文件，清理后立即彻底删除");
        return (true, null);
    }

    private void RunRule(CleanupPlan plan, PlannedRule planned, RuleOutcome outcome, bool dryRun, CancellationToken cancellationToken)
    {
        var rule = planned.Rule;
        if (rule.Action == CleanupAction.EmptyRecycleBin)
        {
            EmptyRecycleBin(plan.DriveRoot, outcome, dryRun);
            return;
        }

        if (rule.Action == CleanupAction.PurgeQuarantine)
        {
            PurgeExpiredQuarantine(outcome, FileAge.Cutoff(rule.MinAgeDays, _time), dryRun, cancellationToken);
            return;
        }

        if (rule.Action is not (CleanupAction.Delete or CleanupAction.Recycle or CleanupAction.Quarantine))
        {
            return;
        }

        if (rule.Action == CleanupAction.Quarantine && !dryRun)
        {
            _quarantineWriter ??= _quarantineStore!.BeginSession(
                string.Join("、", plan.Rules.Where(r => r.Rule.Action == CleanupAction.Quarantine).Select(r => r.Rule.Name)), _time.GetLocalNow());
        }

        var context = new RuleContext(rule, outcome, FileAge.Cutoff(rule.MinAgeDays, _time), dryRun, cancellationToken);
        foreach (var target in planned.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Exclusions = target.Exclusions.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (target.IsFile)
            {
                var file = new FileInfo(target.Path);
                if (file.Exists && TryAnchor(file.DirectoryName!, context))
                {
                    ProcessFile(file, context);
                }
            }
            else if (Directory.Exists(target.Path) && TryAnchor(target.Path, context))
            {
                // 目标目录本身永远保留，只处理里面的内容。
                var emptied = ProcessDirectory(target.Path, depth: 0, context);

                // 例外：旧版本的程序文件夹清空后连文件夹一起删掉，不留下一个空的版本文件夹。
                if (emptied && rule.OldVersionsOnly && rule.Action is CleanupAction.Delete or CleanupAction.Quarantine
                    && (dryRun || _ops.TryDeleteEmptyDirectory(target.Path, context.Anchor!)))
                {
                    outcome.Directories++;
                    if (!dryRun)
                    {
                        Log("removed-dir", rule.Id, target.Path);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 读出清理目标在磁盘上的实际位置。之后每一次删除、移动都和它核对：
    /// 文件的实际位置必须正好是目标里同样的相对路径，路径中途经过链接时拒绝执行。
    /// </summary>
    private static bool TryAnchor(string directory, RuleContext context)
    {
        try
        {
            context.Anchor = PathAnchor.For(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Outcome.AddFailure(directory, 0, "无法确定目录的实际位置：" + Reason(ex));
            return false;
        }
    }

    private void EmptyRecycleBin(string driveRoot, RuleOutcome outcome, bool dryRun)
    {
        if (_ops.QueryRecycleBin(driveRoot) is not { ItemCount: > 0 } bin)
        {
            return;
        }

        if (!dryRun)
        {
            try
            {
                _ops.EmptyRecycleBin(driveRoot);
            }
            catch (IOException ex)
            {
                outcome.AddFailure($"回收站（{driveRoot}）", bin.Bytes, ex.Message);
                Log("failed", outcome.Rule.Id, driveRoot, bin.Bytes, ex.Message);
                return;
            }

            Log("emptied-recycle-bin", outcome.Rule.Id, driveRoot, bin.Bytes, $"{bin.ItemCount} 项");
        }

        outcome.Files = bin.ItemCount;
        outcome.Bytes = bin.Bytes;
        Interlocked.Add(ref _bytesCleaned, bin.Bytes);
    }

    private void PurgeExpiredQuarantine(RuleOutcome outcome, DateTimeOffset? cutoff, bool dryRun, CancellationToken cancellationToken)
    {
        foreach (var session in _quarantineStore!.ListSessions().Where(s => cutoff is not { } c || s.CreatedAt < c))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!dryRun)
            {
                try
                {
                    _quarantineStore.Purge(session);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcome.AddFailure(session.Directory, session.TotalBytes, Reason(ex));
                    Log("failed", outcome.Rule.Id, session.Directory, session.TotalBytes, Reason(ex));
                    continue;
                }

                Log("purged-quarantine", outcome.Rule.Id, session.Directory, session.TotalBytes, $"{session.Items.Count} 个文件，{session.CreatedAt:yyyy-MM-dd HH:mm} 移入");
            }

            outcome.Files += session.Items.Count;
            outcome.Bytes += session.TotalBytes;
            Interlocked.Add(ref _bytesCleaned, session.TotalBytes);
        }
    }

    /// <summary>处理目录中的内容，返回处理完后目录是否（将会）为空。</summary>
    private bool ProcessDirectory(string directory, int depth, RuleContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
        {
            context.Outcome.AddFailure(directory, 0, "目录层级太深，已跳过");
            return false;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ListOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Outcome.AddFailure(directory, 0, "无法读取目录：" + Reason(ex));
            return false;
        }

        var empty = true;
        foreach (var entry in entries)
        {
            if (context.Exclusions.Contains(entry.FullName))
            {
                // 属于其他规则，保持原样。
                empty = false;
                continue;
            }

            if (entry is FileInfo file)
            {
                empty &= ProcessFile(file, context);
                continue;
            }

            var attributes = entry.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) != 0)
            {
                // 目录联接、符号链接、云盘目录：不进入，也不删除。
                context.Outcome.SkippedLinks++;
                empty = false;
                continue;
            }

            var childEmpty = ProcessDirectory(entry.FullName, depth + 1, context);
            if (childEmpty && context.Rule.Action is CleanupAction.Delete or CleanupAction.Quarantine && FileAge.IsOlderThan(entry, context.Cutoff)
                && (context.DryRun || _ops.TryDeleteEmptyDirectory(entry.FullName, context.Anchor!)))
            {
                context.Outcome.Directories++;
                if (!context.DryRun)
                {
                    Log("removed-dir", context.Rule.Id, entry.FullName);
                }
            }
            else
            {
                empty = false;
            }
        }

        return empty;
    }

    /// <summary>处理一个文件，返回它是否（将会）被移除。</summary>
    private bool ProcessFile(FileInfo file, RuleContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _filesVisited);
        var outcome = context.Outcome;
        var attributes = file.Attributes;

        if ((attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) != 0)
        {
            outcome.SkippedLinks++;
            return false;
        }

        if ((attributes & TreeScanner.ProtectedMask) != 0)
        {
            outcome.SkippedProtected++;
            return false;
        }

        if (!FileAge.IsOlderThan(file, context.Cutoff))
        {
            outcome.SkippedTooNew++;
            return false;
        }

        var length = file.Length;
        if (!context.DryRun)
        {
            var action = context.Rule.Action;
            try
            {
                switch (action)
                {
                    case CleanupAction.Recycle:
                        _ops.MoveToRecycleBin(file.FullName, context.Anchor!);
                        break;
                    case CleanupAction.Quarantine:
                        _quarantineWriter!.Move(file.FullName, length, context.Rule.Id, context.Anchor);
                        break;
                    default:
                        _ops.DeleteFile(file.FullName, context.Anchor!);
                        break;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 已经被别的程序删掉了。
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var reason = Reason(ex);
                outcome.AddFailure(file.FullName, length, reason);
                Log("failed", context.Rule.Id, file.FullName, length, reason);
                return false;
            }

            Log(action switch { CleanupAction.Recycle => "recycled", CleanupAction.Quarantine => "quarantined", _ => "deleted" }, context.Rule.Id, file.FullName, length);
        }

        outcome.Files++;
        outcome.Bytes += length;
        Interlocked.Add(ref _bytesCleaned, length);
        return true;
    }

    private void Log(string evt, string? rule = null, string? path = null, long? bytes = null, string? detail = null) =>
        _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = evt, Rule = rule, Path = path, Bytes = bytes, Detail = detail });

    private static string Reason(Exception ex) => (ex.HResult & 0xFFFF) switch
    {
        32 or 33 => "正在被其他程序使用",
        5 => "没有权限",
        _ when ex is UnauthorizedAccessException => "没有权限",
        _ => ex.Message,
    };

    private static long FreeSpace(string driveRoot)
    {
        try
        {
            return new DriveInfo(driveRoot).TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private sealed class RuleContext(CleanupRule rule, RuleOutcome outcome, DateTimeOffset? cutoff, bool dryRun, CancellationToken cancellationToken)
    {
        public CleanupRule Rule { get; } = rule;

        public RuleOutcome Outcome { get; } = outcome;

        public DateTimeOffset? Cutoff { get; } = cutoff;

        public bool DryRun { get; } = dryRun;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public HashSet<string> Exclusions { get; set; } = [];

        /// <summary>当前处理的清理目标及其实际位置。</summary>
        public PathAnchor? Anchor { get; set; }
    }
}
