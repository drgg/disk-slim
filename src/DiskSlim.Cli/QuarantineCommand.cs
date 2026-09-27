using System.Globalization;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Reporting;

namespace DiskSlim.Cli;

/// <summary>diskslim quarantine：列出、恢复、彻底删除隔离区中的内容。</summary>
internal static class QuarantineCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        var store = QuarantineStore.ForDrive(options.DriveRoot);
        return options.QuarantineVerb switch
        {
            QuarantineVerb.Restore => Restore(context, store),
            QuarantineVerb.Purge => Purge(context, store),
            _ => List(context, store),
        };
    }

    private static int List(CliContext context, QuarantineStore store)
    {
        var w = context.Style;
        var sessions = store.ListSessions();
        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine("  ·  隔离区", ConsoleColor.DarkGray);
        w.WriteLine(store.Root, ConsoleColor.DarkGray);
        if (sessions.Any(s => s.IsLegacy))
        {
            w.WriteLine($"{store.LegacyRoot}（旧版工具的隔离区，一项可能是整个文件夹）", ConsoleColor.DarkGray);
        }

        w.WriteLine();

        if (sessions.Count == 0)
        {
            w.WriteLine("  隔离区是空的。");
            w.WriteLine();
            return CliApp.ExitOk;
        }

        var now = DateTimeOffset.Now;
        var idWidth = sessions.Max(s => s.Id.Length) + 2;
        w.WriteLine("  " + "批次".PadRight(idWidth - 2) + TextWidth.PadRight("  移入时间", 20) + TextWidth.PadLeft("数量", 10) + TextWidth.PadLeft("大小", 11) + "   说明", ConsoleColor.DarkGray);
        foreach (var session in sessions)
        {
            var age = (now - session.CreatedAt).TotalDays;
            w.Write("  " + session.Id.PadRight(idWidth));
            w.Write(TextWidth.PadRight($"{session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}", 18));
            w.Write(TextWidth.PadLeft(session.Items.Count.ToString("N0", CultureInfo.InvariantCulture), 10));
            w.Write(TextWidth.PadLeft(ByteSize.Format(session.TotalBytes), 11));
            w.Write("   " + TextWidth.Truncate(session.Description, Math.Max(10, context.Width - idWidth - 50)));
            w.WriteLine(age >= 7 ? $"  （已放 {age:0} 天）" : "", ConsoleColor.DarkYellow);
        }

        w.WriteLine();
        w.WriteLine($"  共 {sessions.Count} 批，{ByteSize.Format(sessions.Sum(s => s.TotalBytes))}。隔离区中的文件还占着空间，彻底删除后才会释放。", ConsoleColor.White);
        w.WriteLine();
        w.WriteLine("  恢复一批：    diskslim quarantine restore <批次>", ConsoleColor.DarkGray);
        w.WriteLine("  彻底删除：    diskslim quarantine purge <批次> --execute", ConsoleColor.DarkGray);
        w.WriteLine("  删除到期的：  diskslim quarantine purge --expired --execute（超过 7 天）", ConsoleColor.DarkGray);
        w.WriteLine();
        return CliApp.ExitOk;
    }

    private static int Restore(CliContext context, QuarantineStore store)
    {
        var w = context.Style;
        if (store.Find(context.Options.QuarantineId!) is not { } session)
        {
            context.Stderr.WriteLine($"隔离区里没有批次 {context.Options.QuarantineId}（运行 diskslim quarantine 查看）。");
            return CliApp.ExitUsage;
        }

        var result = store.Restore(session, SafetyGuard.ForCurrentUser());
        var unit = session.IsLegacy ? "项" : "个文件";
        w.WriteLine($"已恢复 {result.Restored:N0} {unit}（{ByteSize.Format(result.RestoredBytes)}）到原来的位置。", result.Restored > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray);
        if (result.Problems.Count > 0)
        {
            w.WriteLine($"{result.Problems.Count:N0} {unit}没有恢复，仍在隔离区中：", ConsoleColor.DarkYellow);
            foreach (var problem in result.Problems.Take(context.Options.Verbose ? int.MaxValue : 10))
            {
                w.WriteLine($"  {TextWidth.TruncateMiddle(problem.Path, Math.Max(20, context.Width - 40))}  {problem.Reason}", ConsoleColor.DarkGray);
            }

            if (!context.Options.Verbose && result.Problems.Count > 10)
            {
                w.WriteLine("  （加 --verbose 查看全部）", ConsoleColor.DarkGray);
            }
        }

        return CliApp.ExitOk;
    }

    private static int Purge(CliContext context, QuarantineStore store)
    {
        var options = context.Options;
        var w = context.Style;
        var sessions = store.ListSessions();
        IReadOnlyList<QuarantineSession> selected;
        if (options.QuarantineId is { } id)
        {
            if (sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } session)
            {
                context.Stderr.WriteLine($"隔离区里没有批次 {id}（运行 diskslim quarantine 查看）。");
                return CliApp.ExitUsage;
            }

            selected = [session];
        }
        else if (options.PurgeAll)
        {
            selected = sessions;
        }
        else
        {
            var cutoff = DateTimeOffset.Now.AddDays(-options.Days);
            selected = sessions.Where(s => s.CreatedAt < cutoff).ToList();
        }

        if (selected.Count == 0)
        {
            w.WriteLine("没有需要彻底删除的内容。");
            return CliApp.ExitOk;
        }

        var files = selected.Sum(s => s.Items.Count);
        var bytes = selected.Sum(s => s.TotalBytes);
        w.WriteLine(options.Execute ? "将要彻底删除：" : "将要彻底删除（模拟运行，不会删除任何文件）：", ConsoleColor.White);
        foreach (var session in selected)
        {
            w.WriteLine($"  {session.Id}  {session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {DisplayText.QuarantineCount(session)}  {ByteSize.Format(session.TotalBytes)}  {session.Description}");
        }

        w.WriteLine($"共 {files:N0} {(selected.Any(s => s.IsLegacy) ? "项" : "个文件")}，{ByteSize.Format(bytes)}。彻底删除后无法恢复。");
        if (!options.Execute)
        {
            w.WriteLine();
            w.WriteLine("这是模拟运行。确认无误后加 --execute 真正删除。", ConsoleColor.Cyan);
            return CliApp.ExitOk;
        }

        if (!options.AssumeYes)
        {
            if (!context.Interactive)
            {
                context.Stderr.WriteLine("需要确认才能执行：请在终端里运行，或加 --yes 跳过确认。");
                return CliApp.ExitOk;
            }

            w.Write("确定彻底删除吗？输入 y 确认，其他任意键取消：", ConsoleColor.Yellow);
            if (context.Stdin.ReadLine()?.Trim() is not ("y" or "Y" or "yes" or "YES" or "Yes" or "是"))
            {
                w.WriteLine("已取消，没有删除任何文件。");
                return CliApp.ExitOk;
            }
        }

        var logPath = options.LogPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now);
        JsonLinesOperationLog log;
        try
        {
            log = new JsonLinesOperationLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Stderr.WriteLine($"无法创建操作日志 {logPath}：{ex.Message}。为安全起见，没有删除任何文件。");
            return CliApp.ExitError;
        }

        var freeBefore = new DriveInfo(options.DriveRoot).TotalFreeSpace;
        PurgeResult result;
        using (log)
        {
            result = store.Purge(selected, log);
        }

        foreach (var problem in result.Problems)
        {
            w.WriteLine($"  {problem.Path} 删除失败：{problem.Reason}", ConsoleColor.DarkYellow);
        }

        var freeAfter = new DriveInfo(options.DriveRoot).TotalFreeSpace;
        w.WriteLine();
        w.WriteLine($"已彻底删除 {result.Purged} 批。磁盘剩余空间：{ByteSize.Format(freeBefore)} → {ByteSize.Format(freeAfter)}（增加 {ByteSize.Format(Math.Max(0, freeAfter - freeBefore))}）", ConsoleColor.Green);
        w.WriteLine($"操作日志：{log.FilePath}", ConsoleColor.DarkGray);
        return result.Problems.Count == 0 ? CliApp.ExitOk : CliApp.ExitError;
    }

    /// <summary>
    /// --purge-now：把刚移入隔离区的批次立即彻底删除，马上释放空间，并输出结果。全部成功时返回 true。
    /// 删除失败的批次留在隔离区里，可以稍后再删。
    /// </summary>
    internal static bool PurgeNow(CliContext context, IEnumerable<(QuarantineStore Store, string SessionId)> batches, IOperationLog log)
    {
        var w = context.Style;
        var ok = true;
        foreach (var (store, id) in batches)
        {
            if (store.Find(id) is not { } session)
            {
                continue;
            }

            var result = store.Purge([session], log);
            foreach (var problem in result.Problems)
            {
                w.WriteLine($"  立即彻底删除失败：{problem.Reason}。文件还在隔离区里，可以稍后运行 diskslim quarantine purge {id} --execute", ConsoleColor.DarkYellow);
                ok = false;
            }

            if (result.Purged > 0)
            {
                w.WriteLine($"  已立即彻底删除（--purge-now），释放了 {ByteSize.Format(result.PurgedBytes)}。", ConsoleColor.Green);
            }
        }

        return ok;
    }
}
