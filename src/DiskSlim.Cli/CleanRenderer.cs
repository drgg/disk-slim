using System.Globalization;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Cli;

/// <summary>输出 diskslim clean 的计划、确认提示和结果。</summary>
internal sealed class CleanRenderer(ConsoleStyle w, int width, bool verbose)
{
    private const int NumberColumn = 11;
    private const int MethodColumn = 12;

    public void RenderHeader(bool execute, string driveRoot, bool elevated)
    {
        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine(execute ? "  ·  清理" : "  ·  清理（模拟运行，不会删除任何文件）", execute ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
        var free = new DriveInfo(driveRoot).TotalFreeSpace;
        w.WriteLine($"{driveRoot}  剩余 {ByteSize.Format(free)}  ·  管理员权限：{(elevated ? "是" : "否")}", ConsoleColor.DarkGray);
    }

    public void RenderPlan(CleanupPlan plan, CleanupResult preview)
    {
        Section("清理计划", $"选中 {plan.Rules.Count} 项");
        if (plan.Rules.Count == 0)
        {
            w.WriteLine("  没有选中任何项目。");
            return;
        }

        var nameWidth = Math.Clamp(plan.Rules.Max(r => TextWidth.Of(r.Rule.Name)), 12, 28);
        foreach (var risk in new[] { RiskLevel.Low, RiskLevel.Medium })
        {
            var outcomes = preview.Rules.Where(o => o.Rule.Risk == risk).ToList();
            if (outcomes.Count == 0)
            {
                continue;
            }

            w.WriteLine();
            w.WriteLine($"  ● {RiskText.Title(risk)}{(risk == RiskLevel.Medium ? "（已点名）" : "")}", RiskText.Color(risk));
            w.WriteLine("    " + TextWidth.PadRight("项目", nameWidth) + "  " + TextWidth.PadRight("方式", MethodColumn)
                + TextWidth.PadLeft("文件数", NumberColumn) + TextWidth.PadLeft("大小", NumberColumn), ConsoleColor.DarkGray);
            foreach (var outcome in outcomes)
            {
                RenderPlannedRule(outcome, nameWidth);
            }
        }

        RenderPlanTotals(preview);
    }

    public void RenderDryRunFooter(CleanupPlan plan, CliOptions options)
    {
        var available = plan.Available.Where(r => r.Risk == RiskLevel.Medium || options.RuleIds.Count > 0).ToList();
        if (available.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("  还可以点名清理这些项目（用 --rule 选择）：", ConsoleColor.DarkGray);
            var idWidth = available.Max(r => r.Id.Length) + 2;
            var nameWidth = Math.Min(available.Max(r => TextWidth.Of(r.Name)), 30) + 2;
            foreach (var rule in available)
            {
                w.Write("    " + rule.Id.PadRight(idWidth));
                w.Write(TextWidth.PadRight(TextWidth.Truncate(rule.Name, nameWidth - 2), nameWidth));
                w.WriteLine($"[{RiskText.Short(rule.Risk)}] {RiskText.Action(rule.Action)}", ConsoleColor.DarkGray);
            }
        }

        w.WriteLine();
        w.WriteLine("  这是模拟运行，没有删除任何文件。确认无误后运行：", ConsoleColor.Cyan);
        w.WriteLine("    " + ExecuteCommand(options), ConsoleColor.White);
        w.WriteLine();
    }

    public string ConfirmPrompt(CleanupResult preview)
    {
        return $"  {DisplayText.ConfirmSummary(preview)}确定执行吗？输入 y 确认，其他任意键取消：";
    }

    public void RenderResult(CleanupResult result)
    {
        Section("清理结果", result.Cancelled ? "已取消（已完成的部分见日志）" : null);
        var nameWidth = Math.Clamp(result.Rules.Count == 0 ? 12 : result.Rules.Max(r => TextWidth.Of(r.Rule.Name)), 12, 28);
        foreach (var outcome in result.Rules)
        {
            w.Write("    " + TextWidth.PadRight(TextWidth.Truncate(outcome.Rule.Name, nameWidth), nameWidth) + "  ");
            var (text, kind) = DisplayText.ResultSummary(outcome);
            w.WriteLine(text, ColorOf(kind));
            RenderDetails(outcome, executed: true);
        }

        var freed = result.FreeBytesAfter - result.FreeBytesBefore;
        w.WriteLine();
        w.WriteLine("  " + DisplayText.FreeSpaceChange(result), freed > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray);
        if (DisplayText.PendingSpaceNote(result) is { } pending)
        {
            w.WriteLine("  " + pending, ConsoleColor.DarkGray);
        }

        if (result.QuarantineSessionId is { } sessionId)
        {
            w.WriteLine($"  撤销：diskslim quarantine restore {sessionId}", ConsoleColor.Cyan);
            w.WriteLine($"  确认没问题后释放空间：diskslim quarantine purge {sessionId} --execute（超过 7 天的也会在下次 diskslim clean 时清理）", ConsoleColor.DarkGray);
        }

        if (result.LogPath is { } logPath)
        {
            w.WriteLine($"  操作日志：{logPath}", ConsoleColor.DarkGray);
        }

        w.WriteLine();
    }

    private void RenderPlannedRule(RuleOutcome outcome, int nameWidth)
    {
        w.Write("    " + TextWidth.PadRight(TextWidth.Truncate(outcome.Rule.Name, nameWidth), nameWidth) + "  ");

        var (text, kind) = DisplayText.PlanSummary(outcome);
        if (kind != OutcomeKind.Done)
        {
            var skippedNote = kind == OutcomeKind.Nothing && outcome.SkippedTotal > 0 ? $"（{outcome.SkippedTotal:N0} 个文件不满足条件）" : "";
            w.WriteLine(text + skippedNote, ColorOf(kind));
            foreach (var problem in outcome.Planned.Problems)
            {
                w.WriteLine("      " + problem, ConsoleColor.Red);
            }

            RenderDetails(outcome, executed: false);
            return;
        }

        w.WriteLine(TextWidth.PadRight(RiskText.Action(outcome.Rule.Action), MethodColumn)
            + TextWidth.PadLeft(outcome.Files.ToString("N0", CultureInfo.InvariantCulture), NumberColumn)
            + TextWidth.PadLeft(ByteSize.Format(outcome.Bytes), NumberColumn));
        RenderDetails(outcome, executed: false);
    }

    /// <summary>跳过、失败的原因和明细。</summary>
    private void RenderDetails(RuleOutcome outcome, bool executed)
    {
        if (DisplayText.SkipSummary(outcome) is { } skipped && (outcome.Files > 0 || outcome.Failed > 0))
        {
            w.WriteLine("      " + skipped, ConsoleColor.DarkGray);
        }

        if (DisplayText.FailureSummary(outcome, executed) is { } failures)
        {
            w.WriteLine("      " + failures, ConsoleColor.DarkYellow);
            if (verbose)
            {
                foreach (var failure in outcome.Failures.Take(10))
                {
                    w.WriteLine($"        {TextWidth.TruncateMiddle(failure.Path, Math.Max(20, width - 30))}  {failure.Reason}", ConsoleColor.DarkGray);
                }
            }
        }

        if (verbose && !executed)
        {
            foreach (var target in outcome.Planned.Targets.Take(8))
            {
                w.WriteLine("        " + TextWidth.TruncateMiddle(target.Path, Math.Max(20, width - 10)), ConsoleColor.DarkGray);
            }
        }
    }

    private void RenderPlanTotals(CleanupResult preview)
    {
        var total = preview.DeletedBytes + preview.RecycledBytes + preview.QuarantinedBytes;
        Section("合计", $"预计处理 {ByteSize.Format(total)}");
        if (preview.DeletedFiles > 0)
        {
            w.WriteLine($"  永久删除 {preview.DeletedFiles:N0} 项，释放 {ByteSize.Format(preview.DeletedBytes)}");
        }

        if (preview.QuarantinedFiles > 0)
        {
            w.WriteLine($"  移入隔离区 {preview.QuarantinedFiles:N0} 个文件（{ByteSize.Format(preview.QuarantinedBytes)}），可以随时恢复；彻底删除后才会释放空间");
        }

        if (preview.RecycledFiles > 0)
        {
            w.WriteLine($"  移到回收站 {preview.RecycledFiles:N0} 个文件（{ByteSize.Format(preview.RecycledBytes)}），可以找回；清空回收站后才会释放空间");
        }

        if (total > 0)
        {
            w.WriteLine("  正在被使用的文件在执行时会自动跳过，实际释放的空间可能略少。", ConsoleColor.DarkGray);
        }
    }

    private static ConsoleColor ColorOf(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Done => ConsoleColor.Green,
        OutcomeKind.Warning => ConsoleColor.DarkYellow,
        OutcomeKind.Refused => ConsoleColor.Red,
        _ => ConsoleColor.DarkGray,
    };

    private static string ExecuteCommand(CliOptions options)
    {
        var parts = new List<string> { "diskslim clean" };
        if (!string.Equals(options.DriveRoot, SystemInfo.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"-d {options.DriveRoot[0]}");
        }

        if (options.RulesFile is { } rulesFile)
        {
            parts.Add($"--rules \"{rulesFile}\"");
        }

        if (options.RuleIds.Count > 0)
        {
            parts.Add("--rule " + string.Join(",", options.RuleIds));
        }

        parts.Add("--execute");
        return string.Join(" ", parts);
    }

    private void Section(string title, string? subtitle = null)
    {
        w.WriteLine();
        w.Write($"■ {title}", ConsoleColor.Cyan);
        w.WriteLine(subtitle is null ? "" : $"  {subtitle}", ConsoleColor.DarkGray);
    }
}
