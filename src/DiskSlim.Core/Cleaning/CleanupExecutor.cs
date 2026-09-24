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

    public bool Cancelled { get; init; }

    public string? LogPath { get; init; }

    /// <summary>永久删除的字节数（不含移到回收站的）。</summary>
    public long DeletedBytes => Rules.Where(r => r.Rule.Action != CleanupAction.Recycle).Sum(r => r.Bytes);

    public long DeletedFiles => Rules.Where(r => r.Rule.Action != CleanupAction.Recycle).Sum(r => r.Files);

    /// <summary>移到回收站的字节数：暂时还占着空间，清空回收站后才释放。</summary>
    public long RecycledBytes => Rules.Where(r => r.Rule.Action == CleanupAction.Recycle).Sum(r => r.Bytes);

    public long RecycledFiles => Rules.Where(r => r.Rule.Action == CleanupAction.Recycle).Sum(r => r.Files);
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
/// 规则指定的目录本身永远保留；其中清空了的子目录会被删除（仅 delete 方式）。
/// </remarks>
public sealed class CleanupExecutor(IFileOperations? operations = null, IOperationLog? log = null, TimeProvider? time = null)
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

    /// <summary>当前正在处理的规则名，可从其他线程读取以显示进度。</summary>
    public string CurrentRule => _currentRule;

    public long FilesVisited => Interlocked.Read(ref _filesVisited);

    public long BytesCleaned => Interlocked.Read(ref _bytesCleaned);

    public CleanupResult Execute(CleanupPlan plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        var freeBefore = FreeSpace(plan.DriveRoot);
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
            Cancelled = cancelled,
            LogPath = dryRun ? null : _log.FilePath,
        };
    }

    private void RunRule(CleanupPlan plan, PlannedRule planned, RuleOutcome outcome, bool dryRun, CancellationToken cancellationToken)
    {
        var rule = planned.Rule;
        if (rule.Action == CleanupAction.EmptyRecycleBin)
        {
            EmptyRecycleBin(plan.DriveRoot, outcome, dryRun);
            return;
        }

        if (rule.Action is not (CleanupAction.Delete or CleanupAction.Recycle))
        {
            return;
        }

        var context = new RuleContext(rule, outcome, FileAge.Cutoff(rule.MinAgeDays, _time), dryRun, cancellationToken);
        foreach (var target in planned.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Exclusions = target.Exclusions.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (target.IsFile)
            {
                var file = new FileInfo(target.Path);
                if (file.Exists)
                {
                    ProcessFile(file, context);
                }
            }
            else if (Directory.Exists(target.Path))
            {
                // 目标目录本身永远保留，只处理里面的内容。
                ProcessDirectory(target.Path, depth: 0, context);
            }
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
            if (childEmpty && context.Rule.Action == CleanupAction.Delete && FileAge.IsOlderThan(entry, context.Cutoff)
                && (context.DryRun || _ops.TryDeleteEmptyDirectory(entry.FullName)))
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
            var recycle = context.Rule.Action == CleanupAction.Recycle;
            try
            {
                if (recycle)
                {
                    _ops.MoveToRecycleBin(file.FullName);
                }
                else
                {
                    _ops.DeleteFile(file.FullName);
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

            Log(recycle ? "recycled" : "deleted", context.Rule.Id, file.FullName, length);
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
    }
}
