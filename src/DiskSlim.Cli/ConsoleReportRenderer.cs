using System.Globalization;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Cli;

internal static class RiskText
{
    public static readonly IReadOnlyList<RiskLevel> Order = [RiskLevel.Low, RiskLevel.Medium, RiskLevel.High];

    public static string Title(RiskLevel risk) => DisplayText.RiskTitle(risk);

    public static string Short(RiskLevel risk) => DisplayText.RiskShort(risk);

    public static ConsoleColor Color(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => ConsoleColor.Green,
        RiskLevel.Medium => ConsoleColor.Yellow,
        _ => ConsoleColor.Red,
    };

    public static string Action(CleanupAction action) => DisplayText.ActionName(action);

    /// <summary>这一项怎么用 diskslim clean 清理；没有自动清理方式时返回 null。</summary>
    public static string? CleanHint(CleanupRule rule) => (rule.Action, rule.Risk) switch
    {
        (CleanupAction.None, _) or (_, RiskLevel.High) => null,
        (_, RiskLevel.Low) => "可自动清理：diskslim clean（默认包含）",
        _ => $"可自动清理：diskslim clean --rule {rule.Id}",
    };

    public static long CountedBytes(RuleResult result) => DisplayText.CountedBytes(result);
}

/// <summary>把扫描报告输出到控制台。</summary>
internal sealed class ConsoleReportRenderer(ConsoleStyle w, int width, bool verbose)
{
    private const int SizeColumn = 10;

    public void Render(ScanReport report)
    {
        RenderHeader(report);
        RenderDrive(report.Drive);
        if (report.RulesChecked)
        {
            RenderRules(report);
        }

        if (report.SpaceMap is { } map)
        {
            RenderFolders(map);
            RenderLargestFiles(map);
            RenderStats(map);
        }

        RenderNotes(report.Notes);
        w.WriteLine();
    }

    private void RenderHeader(ScanReport report)
    {
        w.Write($"DiskSlim 硬盘瘦身 v{report.ToolVersion}", ConsoleColor.White);
        w.WriteLine("  ·  只读扫描，不会修改任何文件", ConsoleColor.DarkGray);
        w.WriteLine($"{report.GeneratedAt:yyyy-MM-dd HH:mm}  ·  管理员权限：{(report.IsElevated ? "是" : "否")}", ConsoleColor.DarkGray);
    }

    private void RenderDrive(DriveSummary drive)
    {
        Section("磁盘概况");
        var label = string.IsNullOrEmpty(drive.Label) ? "" : $"  “{drive.Label}”";
        w.WriteLine($"  {drive.Root}  {drive.Format}{label}");
        w.WriteLine($"  总容量 {ByteSize.Format(drive.TotalBytes)}  ·  已用 {ByteSize.Format(drive.UsedBytes)}（{Percent(drive.UsedRatio)}）  ·  剩余 {ByteSize.Format(drive.FreeBytes)}");
        var color = drive.UsedRatio >= 0.9 ? ConsoleColor.Red : drive.UsedRatio >= 0.75 ? ConsoleColor.Yellow : ConsoleColor.Green;
        w.Write("  [");
        w.Write(Bar(drive.UsedRatio, 40), color);
        w.WriteLine("]");
    }

    private void RenderRules(ScanReport report)
    {
        Section("可清理项目", $"检查了 {report.RuleCount} 条规则，发现 {report.Rules.Count} 项");
        if (report.Rules.Count == 0)
        {
            w.WriteLine("  没有发现可清理的内容。");
            return;
        }

        var nameWidth = Math.Clamp(report.Rules.Max(r => TextWidth.Of(r.Rule.Name)), 12, 30);
        foreach (var risk in RiskText.Order)
        {
            var items = report.Rules
                .Where(r => r.Rule.Risk == risk)
                .OrderByDescending(RiskText.CountedBytes)
                .ThenByDescending(r => r.TotalBytes)
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var sum = items.Sum(RiskText.CountedBytes);
            w.WriteLine();
            w.Write($"  ● {RiskText.Title(risk)}", RiskText.Color(risk));
            w.WriteLine($"   {(risk == RiskLevel.High ? "合计" : "合计可释放")} {ByteSize.Format(sum)}", ConsoleColor.White);
            w.WriteLine("    " + TextWidth.PadRight("项目", nameWidth) + Right("可释放") + Right("总大小") + Right("文件数"), ConsoleColor.DarkGray);

            foreach (var item in items)
            {
                RenderRule(item, nameWidth);
            }
        }
    }

    private void RenderRule(RuleResult item, int nameWidth)
    {
        var reclaimable = item.Rule.Risk == RiskLevel.High ? "-" : ByteSize.Format(item.ReclaimableBytes);
        w.WriteLine("    " + TextWidth.PadRight(TextWidth.Truncate(item.Rule.Name, nameWidth), nameWidth)
            + Right(reclaimable) + Right(ByteSize.Format(item.TotalBytes)) + Right(item.FileCount.ToString("N0", CultureInfo.InvariantCulture)));

        var adviceWidth = Math.Max(30, width - 10);
        var advice = item.Rule.Advice;
        if (item.Rule.MinAgeDays is > 0 and var days && item.ReclaimableBytes < item.TotalBytes)
        {
            advice = $"（只计入 {days} 天前的文件）" + advice;
        }

        var lines = TextWidth.Wrap(advice, adviceWidth);
        for (var i = 0; i < lines.Count; i++)
        {
            w.WriteLine((i == 0 ? "      → " : "        ") + lines[i], ConsoleColor.DarkGray);
        }

        if (RiskText.CleanHint(item.Rule) is { } hint)
        {
            w.WriteLine("        " + hint, ConsoleColor.DarkCyan);
        }
        else if (Core.SystemTasks.SystemTaskCatalog.ForRule(item.Rule.Id) is { Count: > 0 } systemTasks)
        {
            w.WriteLine($"        可用系统清理：{string.Join(" 或 ", systemTasks.Select(t => "diskslim system run " + t.Id))}（需要管理员权限）", ConsoleColor.DarkCyan);
        }

        if (item.RunningProcesses.Count > 0)
        {
            w.WriteLine($"      ! {string.Join("、", item.RunningProcesses)} 正在运行，清理前请先关闭", ConsoleColor.DarkYellow);
        }

        if (item.InaccessibleDirectories > 0)
        {
            w.WriteLine($"      ! 有 {item.InaccessibleDirectories:N0} 个文件夹无权限读取，实际可能更大（以管理员身份运行可完整统计）", ConsoleColor.DarkYellow);
        }

        if (verbose)
        {
            const int maxShown = 8;
            var matches = item.Matches.OrderByDescending(m => m.TotalBytes).ToList();
            foreach (var match in matches.Take(maxShown))
            {
                w.WriteLine("        " + TextWidth.PadLeft(ByteSize.Format(match.TotalBytes), SizeColumn) + "  " + TextWidth.TruncateMiddle(match.Path, Math.Max(20, width - 22)), ConsoleColor.DarkGray);
            }

            if (matches.Count > maxShown)
            {
                var rest = matches.Skip(maxShown).ToList();
                w.WriteLine($"        {TextWidth.PadLeft(ByteSize.Format(rest.Sum(m => m.TotalBytes)), SizeColumn)}  还有 {rest.Count} 处（完整列表见 --json 报告）", ConsoleColor.DarkGray);
            }
        }
    }

    private void RenderFolders(SpaceMap map)
    {
        Section("空间分布", $"扫描用时 {map.ElapsedSeconds:0.0} 秒");
        var anyIncomplete = false;
        foreach (var view in map.Folders)
        {
            w.WriteLine();
            w.Write($"  {view.Title}  ", ConsoleColor.White);
            w.WriteLine($"{view.Path}  ·  {ByteSize.Format(view.TotalBytes)}", ConsoleColor.DarkGray);
            foreach (var item in view.Items)
            {
                var ratio = view.TotalBytes == 0 ? 0 : (double)item.Bytes / view.TotalBytes;
                anyIncomplete |= item.Incomplete;
                w.Write("    " + TextWidth.PadLeft(ByteSize.Format(item.Bytes), SizeColumn) + "  " + TextWidth.PadLeft(Percent(ratio), 4) + "  ");
                w.Write(Bar(ratio, 12), ConsoleColor.Blue);
                var name = item.Name + (item.Incomplete ? " *" : "");
                w.WriteLine("  " + TextWidth.Truncate(name, Math.Max(20, width - 38)), item.IsOwnFiles ? ConsoleColor.DarkGray : null);
            }
        }

        if (anyIncomplete)
        {
            w.WriteLine();
            w.WriteLine("  * 其中有无权限读取的文件夹，实际大小可能更大。", ConsoleColor.DarkGray);
        }
    }

    private void RenderLargestFiles(SpaceMap map)
    {
        if (map.LargestFiles.Count == 0)
        {
            return;
        }

        Section($"最大的 {map.LargestFiles.Count} 个文件");
        foreach (var file in map.LargestFiles)
        {
            w.Write("    " + TextWidth.PadLeft(ByteSize.Format(file.Bytes), SizeColumn) + "  ");
            w.Write($"{file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd}  ", ConsoleColor.DarkGray);
            w.WriteLine(TextWidth.TruncateMiddle(file.Path, Math.Max(20, width - 30)));
        }
    }

    private void RenderStats(SpaceMap map)
    {
        var s = map.Stats;
        Section("扫描统计");
        w.WriteLine($"  共 {s.Files:N0} 个文件、{s.Directories:N0} 个文件夹，合计 {ByteSize.Format(s.Bytes)}，用时 {map.ElapsedSeconds:0.0} 秒");

        var parts = new List<string>();
        if (s.InaccessibleDirectories > 0)
        {
            parts.Add($"无法读取 {s.InaccessibleDirectories:N0} 个文件夹");
        }

        if (s.SkippedLinks > 0)
        {
            parts.Add($"跳过 {s.SkippedLinks:N0} 个目录链接");
        }

        if (s.CloudOnlyFiles + s.CloudOnlyDirectories > 0)
        {
            parts.Add($"{s.CloudOnlyFiles + s.CloudOnlyDirectories:N0} 个云盘仅在线项目未计入");
        }

        if (parts.Count > 0)
        {
            w.WriteLine("  " + string.Join("  ·  ", parts), ConsoleColor.DarkGray);
        }

        if (map.Errors.Count > 0)
        {
            var shown = verbose ? map.Errors : map.Errors.Take(5).ToList();
            w.WriteLine(verbose ? "  无法读取的文件夹：" : "  无法读取的文件夹（前 5 个，加 --verbose 查看全部）：", ConsoleColor.DarkGray);
            foreach (var error in shown)
            {
                w.WriteLine($"    {TextWidth.TruncateMiddle(error.Path, Math.Max(20, width - 24))}  {error.Message}", ConsoleColor.DarkGray);
            }
        }
    }

    private void RenderNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        Section("说明");
        foreach (var note in notes)
        {
            var lines = TextWidth.Wrap(note, Math.Max(30, width - 6));
            for (var i = 0; i < lines.Count; i++)
            {
                w.WriteLine((i == 0 ? "  · " : "    ") + lines[i], ConsoleColor.DarkGray);
            }
        }
    }

    private void Section(string title, string? subtitle = null)
    {
        w.WriteLine();
        w.Write($"■ {title}", ConsoleColor.Cyan);
        w.WriteLine(subtitle is null ? "" : $"  {subtitle}", ConsoleColor.DarkGray);
    }

    private static string Right(string text) => TextWidth.PadLeft(text, SizeColumn + 1);

    internal static string Percent(double ratio) => (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>固定字符数的进度条：无论终端把方块字符显示成 1 列还是 2 列，每一行都一样宽。</summary>
    internal static string Bar(double ratio, int length)
    {
        var filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * length);
        return new string('█', filled) + new string('░', length - filled);
    }
}
