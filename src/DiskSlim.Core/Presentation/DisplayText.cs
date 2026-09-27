using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Presentation;

public enum OutcomeKind
{
    /// <summary>成功处理了内容。</summary>
    Done,

    /// <summary>没有需要处理的内容。</summary>
    Nothing,

    /// <summary>跳过或部分完成，需要用户留意。</summary>
    Warning,

    /// <summary>被拒绝。</summary>
    Refused,
}

/// <summary>命令行和图形界面共用的显示文字，保证两边说法一致。</summary>
public static class DisplayText
{
    public static string RiskTitle(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "可放心清理",
        RiskLevel.Medium => "需要确认",
        _ => "仅供参考（不建议用工具清理）",
    };

    public static string RiskShort(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "可放心",
        RiskLevel.Medium => "需确认",
        _ => "仅参考",
    };

    public static string ActionName(CleanupAction action) => action switch
    {
        CleanupAction.Delete => "永久删除",
        CleanupAction.Recycle => "移到回收站",
        CleanupAction.EmptyRecycleBin => "清空回收站",
        CleanupAction.Quarantine => "移入隔离区",
        CleanupAction.PurgeQuarantine => "彻底删除",
        _ => "只提示，不自动清理",
    };

    /// <summary>工具能否清理这条规则；不能时返回原因。</summary>
    public static string? WhyNotCleanable(CleanupRule rule) => (rule.Risk, rule.Action) switch
    {
        (RiskLevel.High, _) => "仅供参考：工具不会清理这类内容，请按建议手动处理",
        (_, CleanupAction.None) => "需要手动处理：请按建议操作",
        _ => null,
    };

    /// <summary>“仅供参考”的项目不建议清理，所以只统计总大小，不算作可释放。</summary>
    public static long CountedBytes(RuleResult result) =>
        result.Rule.Risk == RiskLevel.High ? result.TotalBytes : result.ReclaimableBytes;

    /// <summary>被保留（不满足条件）的文件说明；没有时返回 null。</summary>
    public static string? SkipSummary(RuleOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.SkippedTooNew > 0)
        {
            parts.Add($"{outcome.SkippedTooNew:N0} 个太新（{outcome.Rule.MinAgeDays} 天内）");
        }

        if (outcome.SkippedProtected > 0)
        {
            parts.Add($"{outcome.SkippedProtected:N0} 个只读或系统文件");
        }

        if (outcome.SkippedLinks > 0)
        {
            parts.Add($"{outcome.SkippedLinks:N0} 个链接或云盘文件");
        }

        return parts.Count == 0 ? null : "保留：" + string.Join("，", parts);
    }

    /// <summary>处理失败的说明；没有时返回 null。</summary>
    public static string? FailureSummary(RuleOutcome outcome, bool executed)
    {
        if (outcome.Failed == 0)
        {
            return null;
        }

        var reasons = outcome.Failures.GroupBy(f => f.Reason).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(2).ToList();
        var hint = reasons.Any(r => r.Contains("权限")) ? "（以管理员身份运行可以处理更多）" : "";
        return $"{(executed ? "未能处理" : "无法处理")} {outcome.Failed:N0} 项：{string.Join("；", reasons)}{hint}";
    }

    /// <summary>清理计划中一条规则的状态（模拟运行时）。</summary>
    public static (string Text, OutcomeKind Kind) PlanSummary(RuleOutcome outcome)
    {
        switch (outcome.Planned.Status)
        {
            case PlanStatus.ProcessRunning:
                return ($"跳过：{string.Join("、", outcome.Planned.RunningProcesses)} 正在运行，请先关闭", OutcomeKind.Warning);
            case PlanStatus.NothingFound:
                return ("没有找到需要清理的内容", OutcomeKind.Nothing);
            case PlanStatus.Forbidden:
                return ("拒绝执行：目标位于受保护的位置", OutcomeKind.Refused);
        }

        if (outcome.Files == 0)
        {
            return outcome.Failed > 0 ? ("无法处理", OutcomeKind.Warning) : ("没有需要清理的内容", OutcomeKind.Nothing);
        }

        return ($"{ActionName(outcome.Rule.Action)} {outcome.Files:N0} 项，{ByteSize.Format(outcome.Bytes)}", OutcomeKind.Done);
    }

    /// <summary>执行后一条规则的结果。</summary>
    public static (string Text, OutcomeKind Kind) ResultSummary(RuleOutcome outcome)
    {
        if (outcome.Planned.Status != PlanStatus.Ready)
        {
            return PlanSummary(outcome);
        }

        if (outcome.Files == 0 && outcome.Failed == 0 && !outcome.Cancelled)
        {
            return ("没有需要清理的内容", OutcomeKind.Nothing);
        }

        var done = outcome.Rule.Action switch
        {
            CleanupAction.Recycle => $"已移到回收站 {outcome.Files:N0} 个文件（{ByteSize.Format(outcome.Bytes)}）",
            CleanupAction.EmptyRecycleBin => $"已清空 {outcome.Files:N0} 项（{ByteSize.Format(outcome.Bytes)}）",
            CleanupAction.Quarantine when outcome.PurgedAfterQuarantine => $"已彻底删除 {outcome.Files:N0} 个文件（{ByteSize.Format(outcome.Bytes)}）",
            CleanupAction.Quarantine => $"已移入隔离区 {outcome.Files:N0} 个文件（{ByteSize.Format(outcome.Bytes)}），可以撤销",
            CleanupAction.PurgeQuarantine => $"已彻底删除 {outcome.Files:N0} 个文件（{ByteSize.Format(outcome.Bytes)}）",
            _ => $"已删除 {outcome.Files:N0} 个文件（{ByteSize.Format(outcome.Bytes)}）"
                + (outcome.Directories > 0 ? $"、{outcome.Directories:N0} 个空文件夹" : ""),
        };

        if (outcome.Cancelled)
        {
            return (done + "，已取消", OutcomeKind.Warning);
        }

        return (done, outcome.Failed > 0 ? OutcomeKind.Warning : OutcomeKind.Done);
    }

    /// <summary>确认提示中“将要做什么”的描述。</summary>
    /// <param name="purgeQuarantine">移入隔离区的文件会在清理后立即彻底删除。</param>
    public static string ConfirmSummary(CleanupResult preview, bool purgeQuarantine = false)
    {
        var parts = new List<string>();
        if (preview.DeletedFiles > 0)
        {
            parts.Add($"永久删除 {preview.DeletedFiles:N0} 项（{ByteSize.Format(preview.DeletedBytes)}）");
        }

        if (preview.QuarantinedFiles > 0)
        {
            parts.Add($"把 {preview.QuarantinedFiles:N0} 个文件移入隔离区（{ByteSize.Format(preview.QuarantinedBytes)}）");
        }

        if (preview.RecycledFiles > 0)
        {
            parts.Add($"把 {preview.RecycledFiles:N0} 个文件移到回收站（{ByteSize.Format(preview.RecycledBytes)}）");
        }

        var warning = preview.DeletedFiles > 0 ? "永久删除的内容无法恢复。" : "";
        var note = preview.QuarantinedFiles == 0 ? ""
            : purgeQuarantine ? "移入隔离区的文件会在清理后立即彻底删除，不能撤销。"
            : "隔离区中的文件可以随时恢复，彻底删除后才会释放空间。";
        return "将要" + string.Join("，", parts) + "。" + warning + note;
    }

    /// <summary>
    /// 剩余空间紧张、而这次清理有一部分只是移入隔离区或移到回收站（暂时还占着空间）时的提醒；不需要提醒时返回 null。
    /// </summary>
    /// <param name="purgeQuarantine">移入隔离区的内容会在清理后立即彻底删除（这部分不用提醒）。</param>
    public static string? LowSpaceWarning(CleanupResult preview, bool purgeQuarantine = false)
    {
        var quarantined = purgeQuarantine ? 0 : preview.QuarantinedBytes;
        if (!Platform.DiskSpace.IsLow(preview.FreeBytesBefore, preview.DriveTotalBytes) || quarantined + preview.RecycledBytes == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (quarantined > 0)
        {
            parts.Add($"移入隔离区的 {ByteSize.Format(preview.QuarantinedBytes)} 要彻底删除后才会腾出空间");
        }

        if (preview.RecycledBytes > 0)
        {
            parts.Add($"移到回收站的 {ByteSize.Format(preview.RecycledBytes)} 要清空回收站后才会腾出空间");
        }

        return $"磁盘只剩 {ByteSize.Format(preview.FreeBytesBefore)}：" + string.Join("，", parts) + "。";
    }

    /// <summary>重复文件、压缩包这类要移入隔离区的内容：剩余空间紧张时的提醒；不需要提醒时返回 null。</summary>
    public static string? QuarantineLowSpaceWarning(string driveRoot, long quarantinedBytes)
    {
        var (free, total) = Platform.DiskSpace.Query(driveRoot);
        return quarantinedBytes > 0 && Platform.DiskSpace.IsLow(free, total)
            ? $"{driveRoot} 只剩 {ByteSize.Format(free)}：移入隔离区的 {ByteSize.Format(quarantinedBytes)} 要彻底删除后才会腾出空间。"
            : null;
    }

    /// <summary>移到回收站、移入隔离区这类“暂时还占着空间”的内容的说明；没有时返回 null。</summary>
    public static string? PendingSpaceNote(CleanupResult result)
    {
        var parts = new List<string>();
        if (result.QuarantinedBytes > 0 && !result.QuarantinePurged)
        {
            parts.Add($"{ByteSize.Format(result.QuarantinedBytes)} 移入了隔离区（可以恢复，彻底删除后才释放空间）"
                + (result.QuarantinePurgeError is { } error ? $"，立即彻底删除没有成功：{error}" : ""));
        }

        if (result.RecycledBytes > 0)
        {
            parts.Add($"{ByteSize.Format(result.RecycledBytes)} 移到了回收站（清空回收站后才释放空间）");
        }

        return parts.Count == 0 ? null : "另有 " + string.Join("，", parts) + "。";
    }

    /// <summary>一批隔离内容有多少项：新版都是文件，旧版工具的一项可能是整个文件夹。</summary>
    public static string QuarantineCount(Quarantine.QuarantineSession session) =>
        session.Items.Any(i => i.IsDirectory) ? $"{session.Items.Count:N0} 项" : $"{session.Items.Count:N0} 个文件";

    /// <summary>磁盘剩余空间的变化。</summary>
    public static string FreeSpaceChange(CleanupResult result)
    {
        var freed = result.FreeBytesAfter - result.FreeBytesBefore;
        var change = freed >= 0 ? $"增加 {ByteSize.Format(freed)}" : $"减少 {ByteSize.Format(-freed)}，可能有其他程序同时在写入";
        return $"磁盘剩余空间：{ByteSize.Format(result.FreeBytesBefore)} → {ByteSize.Format(result.FreeBytesAfter)}（{change}）";
    }

    // ───── 空间变化 ─────

    /// <summary>带正负号的大小，例如 “+1.50 GB”“-300 MB”“0 B”。</summary>
    public static string SignedSize(long bytes) => bytes switch
    {
        > 0 => "+" + ByteSize.Format(bytes),
        < 0 => ByteSize.Format(bytes),
        _ => "0 B",
    };

    /// <summary>快照里的目录大小；没有单独记录时显示为 “&lt; 门槛”。</summary>
    public static string SnapshotSize(long? bytes, long threshold) => bytes is { } b ? ByteSize.Format(b) : "< " + ByteSize.Format(threshold);

    /// <summary>一段时间的说法：“3 分钟”“5 小时”“7 天”。</summary>
    public static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "不到 1 分钟",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} 分钟",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} 小时",
        _ => $"{(int)Math.Round(span.TotalDays)} 天",
    };

    /// <summary>过去的某个时间，例如 “2026-09-18 10:03（7 天前）”。</summary>
    public static string Since(DateTimeOffset time, DateTimeOffset now) =>
        $"{time.ToLocalTime():yyyy-MM-dd HH:mm}（{(now - time < TimeSpan.FromMinutes(1) ? "刚才" : Duration(now - time) + "前")}）";

    /// <summary>某个位置变化的一行说明，例如 “120 MB → 1.62 GB”，或 “其他内容（整个文件夹 +2.00 GB）”。</summary>
    public static string GrowthDetail(History.GrowthItem item, long threshold) => item.IsRemainder
        ? $"除上面列出的子文件夹以外的变化（整个文件夹 {SignedSize(item.FolderDelta)}）"
        : $"{SnapshotSize(item.OldBytes, threshold)} → {SnapshotSize(item.NewBytes, threshold)}";

    /// <summary>空间变化的总览，例如 “9月18日 → 9月25日（7 天）：已用空间 +3.20 GB”。</summary>
    public static string GrowthHeadline(History.GrowthReport report)
    {
        var from = report.From.Time.ToLocalTime();
        var to = report.To.Time.ToLocalTime();
        return $"{from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}（{Duration(report.Span)}）：已用空间 {SignedSize(report.UsedDelta)}"
            + $"（剩余 {ByteSize.Format(report.From.FreeBytes)} → {ByteSize.Format(report.To.FreeBytes)}）";
    }

    /// <summary>比较结果需要留意的地方；没有时返回 null。</summary>
    public static string? GrowthCaveat(History.GrowthReport report) => report.ElevationDiffers
        ? "两次扫描的权限不同（其中一次以管理员身份运行），系统目录的变化可能只是能否读取的区别。"
        : null;
}
