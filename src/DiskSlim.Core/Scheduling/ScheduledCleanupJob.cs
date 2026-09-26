using System.Diagnostics;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.History;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Scheduling;

/// <summary>
/// 定时清理实际做的事：清理所有“可放心清理”的项目（和不带参数的 diskslim clean 完全一样），
/// 写操作日志，然后扫描一次磁盘、记下空间快照，最后追加一条运行记录。
/// </summary>
/// <remarks>
/// 只会清理风险为“可放心清理”并且有清理方式的规则；相关程序正在运行的项目跳过；
/// “需要确认”的项目（回收站、NuGet 包目录……）永远不会被定时清理。
/// </remarks>
public sealed class ScheduledCleanupJob
{
    internal const string MutexName = @"Local\DiskSlim.ScheduledCleanup";

    private readonly RuleSet _rules;
    private readonly string _driveRoot;
    private readonly ScheduleManager? _manager;
    private readonly SnapshotStore? _history;
    private readonly string _logDirectory;
    private readonly Quarantine.QuarantineStore? _quarantine;
    private readonly IReadOnlySet<string>? _runningProcesses;
    private readonly TimeProvider _time;

    /// <param name="manager">运行记录写到这里；为 null 时不记录。</param>
    /// <param name="history">空间快照存到这里；为 null 时不扫描、不记快照（测试用）。</param>
    /// <param name="logDirectory">操作日志的目录，默认 %LOCALAPPDATA%\DiskSlim\logs。</param>
    public ScheduledCleanupJob(
        RuleSet rules,
        string driveRoot,
        ScheduleManager? manager,
        SnapshotStore? history,
        string? logDirectory = null,
        Quarantine.QuarantineStore? quarantine = null,
        IReadOnlySet<string>? runningProcesses = null,
        TimeProvider? time = null)
    {
        _rules = rules;
        _driveRoot = driveRoot;
        _manager = manager;
        _history = history;
        _logDirectory = logDirectory ?? Path.GetDirectoryName(JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now))!;
        _quarantine = quarantine;
        _runningProcesses = runningProcesses;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>定时清理会处理的规则：“可放心清理”并且有清理方式的。</summary>
    public static IReadOnlyList<CleanupRule> AutomaticRules(RuleSet rules) =>
        rules.Rules.Where(r => r.Risk == RiskLevel.Low && r.Action != CleanupAction.None).ToList();

    /// <summary>当前阶段，可从其他线程读取以显示进度。</summary>
    public string Stage { get; private set; } = "准备中";

    public ScheduledRunRecord Run(CancellationToken cancellationToken = default)
    {
        var started = _time.GetLocalNow();

        // 定时任务和手动的 “diskslim schedule run” 不会同时清理。
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // 上一次运行的进程异常退出了
        }

        if (!acquired)
        {
            return new ScheduledRunRecord { Time = started, Success = false, Message = "另一次定时清理正在运行，这次没有执行。" };
        }

        var stopwatch = Stopwatch.StartNew();
        ScheduledRunRecord record;
        try
        {
            record = RunCore(started, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            record = new ScheduledRunRecord { Time = started, Success = false, Message = "运行失败：" + ex.Message };
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        record = record with { ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1) };
        try
        {
            _manager?.AppendRun(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 运行记录写不进去不影响清理结果，操作日志里有完整记录。
        }

        return record;
    }

    private ScheduledRunRecord RunCore(DateTimeOffset started, CancellationToken cancellationToken)
    {
        Stage = "检查要清理的项目";
        var planner = new CleanupPlanner(SafetyGuard.ForCurrentUser(), _runningProcesses ?? SystemInfo.RunningProcessNames(), _quarantine, _time);
        var plan = planner.Plan(_rules, _driveRoot, []);

        // 双保险：定时运行只处理“可放心清理”的项目。
        if (plan.Rules.Any(r => r.Rule.Risk != RiskLevel.Low))
        {
            throw new InvalidOperationException("定时清理只能处理“可放心清理”的项目。");
        }

        JsonLinesOperationLog log;
        try
        {
            log = new JsonLinesOperationLog(Path.Join(_logDirectory, $"scheduled-{started:yyyyMMdd-HHmmss}.jsonl"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScheduledRunRecord { Time = started, Success = false, Message = $"无法创建操作日志（{ex.Message}），为安全起见没有清理。" };
        }

        CleanupResult result;
        using (log)
        {
            Stage = "清理中";
            result = new CleanupExecutor(log: log, time: _time, quarantine: _quarantine).Execute(plan, dryRun: false, cancellationToken);
        }

        var skipped = result.Rules.Where(r => r.Planned.Status == PlanStatus.ProcessRunning).Select(r => r.Rule.Name).ToList();
        var snapshotSaved = !result.Cancelled && SaveSnapshot(cancellationToken);
        var freed = result.FreeBytesAfter - result.FreeBytesBefore;
        var message = result.Cancelled ? "已取消，完成的部分记在操作日志里。"
            : result.TotalFiles == 0 ? "没有需要清理的内容。"
            : $"清理了 {result.TotalFiles:N0} 个文件。{DisplayText.FreeSpaceChange(result)}";
        if (skipped.Count > 0)
        {
            message += $"{string.Join("、", skipped)} 的相关程序正在运行，这次跳过。";
        }

        return new ScheduledRunRecord
        {
            Time = started,
            Success = !result.Cancelled && result.Rules.All(r => r.Planned.Status != PlanStatus.Forbidden),
            Message = message,
            FreedBytes = freed,
            CleanedBytes = result.Rules.Sum(r => r.Bytes),
            Files = result.TotalFiles,
            Skipped = skipped,
            Failed = result.Rules.Sum(r => r.Failed),
            LogPath = log.FilePath,
            SnapshotSaved = snapshotSaved,
        };
    }

    /// <summary>清理后扫描一次，记下空间快照，供“空间变化”比较。失败不影响清理结果。</summary>
    private bool SaveSnapshot(CancellationToken cancellationToken)
    {
        if (_history is null)
        {
            return false;
        }

        Stage = "记录空间快照";
        try
        {
            var builder = new DiskReportBuilder();
            var report = builder.Build(new ReportOptions { DriveRoot = _driveRoot, Rules = _rules, TopCount = 1 }, cancellationToken);
            if (builder.MapResult is not { } map)
            {
                return false;
            }

            _history.Save(SpaceSnapshot.Create(report, map));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            return false;
        }
    }
}
