using DiskSlim.Core.Formatting;
using DiskSlim.Core.History;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Cli;

/// <summary>diskslim growth：比较保存下来的空间快照，找出空间涨在哪里。只读取快照，不扫描磁盘。</summary>
internal static class GrowthCommand
{
    private const int ListLimit = 60;

    public static int Run(CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var drive = options.DriveRoot;
        var history = context.History.History(drive);

        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine($"  ·  空间变化  {drive}", ConsoleColor.DarkGray);
        w.WriteLine();

        if (options.ListHistory)
        {
            return List(context, history);
        }

        var detailed = history.Where(h => h.HasDetails).ToList();
        if (detailed.Count == 0)
        {
            w.WriteLine($"  还没有 {drive} 的扫描记录。运行 diskslim 或打开图形界面做一次完整扫描后，会自动记下空间快照。");
            w.WriteLine();
            return CliApp.ExitOk;
        }

        if (context.History.Load(drive, detailed[^1]) is not { } latest)
        {
            context.Stderr.WriteLine($"无法读取最近一次的空间快照（{detailed[^1].File}），文件可能已损坏。");
            return CliApp.ExitError;
        }

        var baseline = GrowthAnalyzer.PickBaseline(history, latest.Time, TimeSpan.FromDays(options.Days));
        if (baseline is null)
        {
            w.WriteLine($"  只有一次扫描记录（{latest.Time.ToLocalTime():yyyy-MM-dd HH:mm}）。以后再做一次完整扫描，就能看到空间变化。");
            w.WriteLine();
            return CliApp.ExitOk;
        }

        if (context.History.Load(drive, baseline) is not { } old)
        {
            context.Stderr.WriteLine($"无法读取 {baseline.Time.ToLocalTime():yyyy-MM-dd HH:mm} 的空间快照（{baseline.File}），文件可能已损坏。");
            return CliApp.ExitError;
        }

        var report = GrowthAnalyzer.Compare(old, latest, options.MinSize ?? GrowthAnalyzer.DefaultMinBytes, options.Top);
        Render(context, report);

        var age = DateTimeOffset.Now - latest.Time;
        if (age > TimeSpan.FromHours(1))
        {
            w.WriteLine($"  最近一次扫描是 {DisplayText.Duration(age)}前。想和现在比较，先运行 diskslim 重新扫描。", ConsoleColor.DarkGray);
        }

        w.WriteLine("  和其他时间比较：diskslim growth --days 30（--days 0 表示上一次扫描）", ConsoleColor.DarkGray);
        w.WriteLine("  所有扫描记录：  diskslim growth list", ConsoleColor.DarkGray);
        w.WriteLine();
        return CliApp.ExitOk;
    }

    /// <summary>
    /// 完整扫描后调用：先和 7 天前比较并打印一句总结，再把这次扫描记成快照。
    /// 保存失败不影响扫描结果，只提示一句。
    /// </summary>
    internal static void RecordScan(CliContext context, ScanReport report, ScanResult map)
    {
        var w = context.Style;
        SpaceSnapshot snapshot;
        try
        {
            snapshot = SpaceSnapshot.Create(report, map);
        }
        catch (ArgumentException)
        {
            return; // 只扫描了一部分目录，不能和其他快照比较
        }

        var growth = context.History.CompareWithPast(snapshot, TimeSpan.FromDays(7), top: 1);
        string? saveError = null;
        try
        {
            context.History.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saveError = ex.Message;
        }

        if (growth is not null)
        {
            w.WriteLine($"空间变化：和 {DisplayText.Since(growth.From.Time, snapshot.Time)}的扫描相比，已用空间 {DisplayText.SignedSize(growth.UsedDelta)}。", ConsoleColor.White);
            if (growth.Grown.FirstOrDefault() is { } top)
            {
                w.Write("  增长最多：" + TextWidth.PadLeft(DisplayText.SignedSize(top.Delta), 9), ConsoleColor.DarkYellow);
                w.WriteLine("  " + TextWidth.TruncateMiddle(top.Path, Math.Max(20, context.Width - 25)));
            }

            w.WriteLine("  详情：diskslim growth", ConsoleColor.DarkGray);
        }
        else if (saveError is null)
        {
            w.WriteLine("已记下这次扫描的空间快照。以后再扫描时，运行 diskslim growth 可以看到空间涨在哪里。", ConsoleColor.DarkGray);
        }

        if (saveError is not null)
        {
            w.WriteLine($"没能保存空间快照：{saveError}", ConsoleColor.DarkYellow);
        }

        w.WriteLine();
    }

    private static void Render(CliContext context, GrowthReport report)
    {
        var w = context.Style;
        var width = Math.Max(60, context.Width);
        foreach (var line in TextWidth.Wrap(DisplayText.GrowthHeadline(report), width - 2))
        {
            w.WriteLine("  " + line, ConsoleColor.White);
        }

        w.WriteLine($"  扫描到的文件 {DisplayText.SignedSize(report.ScannedDelta)}。两者的差别通常来自系统还原点、无权限读取的目录和 NTFS 元数据。", ConsoleColor.DarkGray);
        if (DisplayText.GrowthCaveat(report) is { } caveat)
        {
            w.WriteLine("  " + caveat, ConsoleColor.DarkYellow);
        }

        w.WriteLine();
        var threshold = report.To.MinDirectoryBytes;
        RenderItems(context, $"增长最多的位置（变化不小于 {ByteSize.Format(report.MinBytes)}）", report.Grown, threshold, ConsoleColor.DarkYellow);
        RenderItems(context, "减少最多的位置", report.Shrunk, threshold, ConsoleColor.Green);

        if (report.Rules is { Count: > 0 } rules)
        {
            w.WriteLine($"规则项目的变化（不小于 {ByteSize.Format(GrowthAnalyzer.DefaultRuleMinBytes)}）", ConsoleColor.White);
            var nameWidth = Math.Min(28, rules.Max(r => TextWidth.Of(r.Name)) + 2);
            foreach (var rule in rules.Take(context.Options.Top))
            {
                w.Write("  " + TextWidth.PadLeft(DisplayText.SignedSize(rule.Delta), 10), rule.Delta > 0 ? ConsoleColor.DarkYellow : ConsoleColor.Green);
                w.Write("  " + TextWidth.PadRight(TextWidth.Truncate(rule.Name, nameWidth), nameWidth));
                w.WriteLine($"{ByteSize.Format(rule.OldBytes)} → {ByteSize.Format(rule.NewBytes)}", ConsoleColor.DarkGray);
            }

            w.WriteLine();
        }
    }

    private static void RenderItems(CliContext context, string title, IReadOnlyList<GrowthItem> items, long threshold, ConsoleColor color)
    {
        var w = context.Style;
        w.WriteLine(title, ConsoleColor.White);
        if (items.Count == 0)
        {
            w.WriteLine("  没有。", ConsoleColor.DarkGray);
            w.WriteLine();
            return;
        }

        var pathWidth = Math.Max(20, context.Width - 16);
        foreach (var item in items)
        {
            w.Write("  " + TextWidth.PadLeft(DisplayText.SignedSize(item.Delta), 10), color);
            w.WriteLine("  " + TextWidth.TruncateMiddle(item.Path, pathWidth));
            w.WriteLine(new string(' ', 14) + DisplayText.GrowthDetail(item, threshold), ConsoleColor.DarkGray);
        }

        w.WriteLine();
    }

    private static int List(CliContext context, IReadOnlyList<SnapshotSummary> history)
    {
        var w = context.Style;
        if (history.Count == 0)
        {
            w.WriteLine("  还没有扫描记录。");
            w.WriteLine();
            return CliApp.ExitOk;
        }

        var shown = context.Options.Verbose ? history : history.TakeLast(ListLimit).ToList();
        w.WriteLine($"  共 {history.Count} 次扫描，最早一次是 {history[0].Time.ToLocalTime():yyyy-MM-dd HH:mm}。", ConsoleColor.White);
        w.WriteLine();
        w.WriteLine("  " + TextWidth.PadRight("时间", 18) + TextWidth.PadLeft("已用", 11) + TextWidth.PadLeft("剩余", 11) + TextWidth.PadLeft("和上一次相比", 16), ConsoleColor.DarkGray);

        SnapshotSummary? previous = history.Count > shown.Count ? history[history.Count - shown.Count - 1] : null;
        foreach (var entry in shown)
        {
            w.Write("  " + TextWidth.PadRight($"{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm}", 18));
            w.Write(TextWidth.PadLeft(ByteSize.Format(entry.UsedBytes), 11));
            w.Write(TextWidth.PadLeft(ByteSize.Format(entry.FreeBytes), 11));
            if (previous is not null)
            {
                var delta = entry.UsedBytes - previous.UsedBytes;
                w.Write(TextWidth.PadLeft(DisplayText.SignedSize(delta), 16), delta > 0 ? ConsoleColor.DarkYellow : ConsoleColor.Green);
            }

            w.WriteLine(entry.HasDetails ? "" : "   （明细已按保留期限删除）", ConsoleColor.DarkGray);
            previous = entry;
        }

        if (shown.Count < history.Count)
        {
            w.WriteLine($"  （更早的 {history.Count - shown.Count} 条没有显示，加 --verbose 查看全部）", ConsoleColor.DarkGray);
        }

        w.WriteLine();
        w.WriteLine("  最近 2 天的快照全部保留，90 天以内每天保留一份，用于 diskslim growth 比较。", ConsoleColor.DarkGray);
        w.WriteLine();
        return CliApp.ExitOk;
    }
}
