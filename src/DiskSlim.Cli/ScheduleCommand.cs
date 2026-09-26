using DiskSlim.Core.Platform;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Scheduling;

namespace DiskSlim.Cli;

/// <summary>diskslim schedule：查看、开启、关闭定时清理，或者立即按定时清理的方式运行一次。</summary>
internal static class ScheduleCommand
{
    public static int Run(CliContext context)
    {
        var w = context.Style;
        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine("  ·  定时清理", ConsoleColor.DarkGray);
        w.WriteLine();

        return context.Options.ScheduleVerb switch
        {
            ScheduleVerb.Enable => Enable(context),
            ScheduleVerb.Disable => Disable(context),
            ScheduleVerb.Run => RunNow(context),
            _ => Status(context),
        };
    }

    private static int Status(CliContext context)
    {
        var w = context.Style;
        var manager = context.Schedule;
        var status = manager.GetStatusAsync().GetAwaiter().GetResult();
        if (status.Settings is { } settings)
        {
            w.Write("  状态：", ConsoleColor.DarkGray);
            w.WriteLine(status.IsActive ? $"已开启 · {settings.Describe()}" : $"已开启，但现在不会运行 · {settings.Describe()}", status.IsActive ? ConsoleColor.Green : ConsoleColor.DarkYellow);
            if (status.IsActive)
            {
                var next = settings.NextRun(DateTime.Now);
                w.WriteLine($"  下次：{next:yyyy-MM-dd}（{ScheduleSettings.DayName(next.DayOfWeek)}）{next:HH:mm}；错过的（关机、睡眠）会在下次开机后补上，使用电池时不运行", ConsoleColor.DarkGray);
            }

            w.WriteLine($"  程序：{settings.Program}", ConsoleColor.DarkGray);
        }
        else
        {
            w.WriteLine("  状态：未开启", ConsoleColor.White);
        }

        if (status.Problem is { } problem)
        {
            w.WriteLine("  " + problem, ConsoleColor.DarkYellow);
        }

        w.WriteLine();
        RenderAutomaticRules(context);

        var runs = manager.RecentRuns(5);
        if (runs.Count > 0)
        {
            w.WriteLine("最近的运行", ConsoleColor.White);
            foreach (var run in runs)
            {
                w.Write($"  {run.Time.ToLocalTime():yyyy-MM-dd HH:mm}  ");
                w.Write(run.Success ? "✓ " : "✗ ", run.Success ? ConsoleColor.Green : ConsoleColor.DarkYellow);
                w.WriteLine(TextWidth.Truncate(run.Message, Math.Max(20, context.Width - 22)));
            }

            if (runs[0].LogPath is { } log)
            {
                w.WriteLine($"  最近一次的操作日志：{log}", ConsoleColor.DarkGray);
            }

            w.WriteLine();
        }

        w.WriteLine(status.Settings is null
            ? "  开启：diskslim schedule enable（默认每周日 12:00，可以用 --weekly sat --at 20:00 或 --daily 修改）"
            : "  修改时间：diskslim schedule enable --weekly sat --at 20:00     关闭：diskslim schedule disable", ConsoleColor.DarkGray);
        w.WriteLine("  先看看会清理什么：diskslim schedule run", ConsoleColor.DarkGray);
        w.WriteLine();
        return CliApp.ExitOk;
    }

    private static int Enable(CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var manager = context.Schedule;
        if (!IsSystemDrive(context))
        {
            return CliApp.ExitUsage;
        }

        if (ScheduleManager.FindDesktopApp() is not { } program)
        {
            context.Stderr.WriteLine("找不到 DiskSlimApp.exe：定时清理由它在后台运行（不会弹出窗口）。请把 diskslim.exe 和 DiskSlimApp.exe 放在同一个文件夹里。");
            return CliApp.ExitError;
        }

        var current = manager.LoadSettings();
        var frequency = options.Frequency ?? current?.Frequency ?? ScheduleFrequency.Weekly;
        var settings = new ScheduleSettings
        {
            Program = program,
            Frequency = frequency,
            Day = options.Day ?? current?.Day ?? DayOfWeek.Sunday,
            Time = options.At ?? current?.Time ?? new TimeOnly(12, 0),
        };

        w.WriteLine($"  将要开启定时清理：{settings.Describe()}，以你的身份（普通权限）在后台运行。", ConsoleColor.White);
        w.WriteLine("  只在你登录时运行；错过的会在下次开机后补上；使用电池时不运行；每次最长 1 小时。", ConsoleColor.DarkGray);
        w.WriteLine();
        RenderAutomaticRules(context);

        if (!options.AssumeYes)
        {
            if (!context.Interactive)
            {
                context.Stderr.WriteLine("需要确认才能开启：请在终端里运行，或加 --yes 跳过确认。");
                return CliApp.ExitOk;
            }

            w.Write("确定开启吗？以后会按时自动清理上面这些项目。输入 y 确认，其他任意键取消：", ConsoleColor.Yellow);
            if (context.Stdin.ReadLine()?.Trim() is not ("y" or "Y" or "yes" or "YES" or "Yes" or "是"))
            {
                w.WriteLine("已取消，没有做任何改动。");
                return CliApp.ExitOk;
            }
        }

        if (manager.EnableAsync(settings, DateTimeOffset.Now).GetAwaiter().GetResult() is { } error)
        {
            context.Stderr.WriteLine(error);
            return CliApp.ExitError;
        }

        var next = settings.NextRun(DateTime.Now);
        w.WriteLine($"已开启定时清理：{settings.Describe()}。下次运行：{next:yyyy-MM-dd HH:mm}。", ConsoleColor.Green);
        w.WriteLine($"  在“任务计划程序”里的名字：{manager.TaskName}", ConsoleColor.DarkGray);
        w.WriteLine("  关闭：diskslim schedule disable", ConsoleColor.DarkGray);
        return CliApp.ExitOk;
    }

    private static int Disable(CliContext context)
    {
        var w = context.Style;
        var wasEnabled = context.Schedule.IsEnabled;
        if (context.Schedule.DisableAsync().GetAwaiter().GetResult() is { } error)
        {
            context.Stderr.WriteLine(error);
            return CliApp.ExitError;
        }

        w.WriteLine(wasEnabled ? "已关闭定时清理。" : "定时清理本来就没有开启。", ConsoleColor.Green);
        return CliApp.ExitOk;
    }

    /// <summary>不带 --execute 时模拟（和 diskslim clean 一样）；带 --execute 时按定时清理的方式真正运行一次。</summary>
    private static int RunNow(CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        if (!IsSystemDrive(context))
        {
            return CliApp.ExitUsage;
        }

        if (!options.Execute)
        {
            w.WriteLine("  定时清理做的事和不带参数的 diskslim clean 一样：清理所有“可放心清理”的项目。下面是模拟结果：", ConsoleColor.DarkGray);
            w.WriteLine();
            var exitCode = CleanCommand.Run(context with { Options = options with { Command = CliCommand.Clean, RuleIds = [] } });
            w.WriteLine("  加 --execute 可以立即按定时清理的方式运行一次（清理、写日志、记下空间快照和运行记录）。", ConsoleColor.DarkGray);
            return exitCode;
        }

        if (!options.AssumeYes)
        {
            if (!context.Interactive)
            {
                context.Stderr.WriteLine("需要确认才能执行：请在终端里运行，或加 --yes 跳过确认。");
                return CliApp.ExitOk;
            }

            RenderAutomaticRules(context);
            w.Write("现在就清理上面这些项目吗？永久删除的内容无法恢复。输入 y 确认，其他任意键取消：", ConsoleColor.Yellow);
            if (context.Stdin.ReadLine()?.Trim() is not ("y" or "Y" or "yes" or "YES" or "Yes" or "是"))
            {
                w.WriteLine("已取消，没有删除任何文件。");
                return CliApp.ExitOk;
            }
        }

        var job = new ScheduledCleanupJob(context.Rules, options.DriveRoot, context.Schedule, options.NoHistory ? null : context.History);
        var record = CliApp.RunWithProgress(() => job.Run(context.CancellationToken), _ => job.Stage, context);
        w.WriteLine();
        w.WriteLine((record.Success ? "✓ " : "✗ ") + record.Message, record.Success ? ConsoleColor.Green : ConsoleColor.DarkYellow);
        if (record.Failed > 0)
        {
            w.WriteLine($"  {record.Failed:N0} 个文件正在使用或没有权限，已跳过。", ConsoleColor.DarkGray);
        }

        if (record.LogPath is { } log)
        {
            w.WriteLine($"  操作日志：{log}", ConsoleColor.DarkGray);
        }

        return record.Success ? CliApp.ExitOk : CliApp.ExitError;
    }

    private static void RenderAutomaticRules(CliContext context)
    {
        var w = context.Style;
        var rules = ScheduledCleanupJob.AutomaticRules(context.Rules);
        w.WriteLine($"会自动清理的项目（共 {rules.Count} 项，都是“可放心清理”的；相关程序正在运行时跳过）：", ConsoleColor.White);
        foreach (var rule in rules)
        {
            var detail = rule.MinAgeDays is > 0 && rule.Action != Core.Rules.CleanupAction.PurgeQuarantine ? $"（只清理 {rule.MinAgeDays} 天前的文件）" : "";
            w.WriteLine($"  · {rule.Name}{detail}");
        }

        w.WriteLine("“需要确认”的项目（回收站、下载的安装包、NuGet 包目录……）永远不会被自动清理。", ConsoleColor.DarkGray);
        w.WriteLine();
    }

    private static bool IsSystemDrive(CliContext context)
    {
        if (string.Equals(context.Options.DriveRoot, SystemInfo.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        context.Stderr.WriteLine($"定时清理只用于系统盘（{SystemInfo.SystemDriveRoot}）：清理规则针对的都是系统盘上的缓存和临时文件。");
        return false;
    }
}
