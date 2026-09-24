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
    public static string ConfirmSummary(CleanupResult preview)
    {
        var parts = new List<string>();
        if (preview.DeletedFiles > 0)
        {
            parts.Add($"永久删除 {preview.DeletedFiles:N0} 项（{ByteSize.Format(preview.DeletedBytes)}）");
        }

        if (preview.RecycledFiles > 0)
        {
            parts.Add($"把 {preview.RecycledFiles:N0} 个文件移到回收站（{ByteSize.Format(preview.RecycledBytes)}）");
        }

        return "将要" + string.Join("，", parts) + "。永久删除的内容无法恢复。";
    }

    /// <summary>磁盘剩余空间的变化。</summary>
    public static string FreeSpaceChange(CleanupResult result)
    {
        var freed = result.FreeBytesAfter - result.FreeBytesBefore;
        var change = freed >= 0 ? $"增加 {ByteSize.Format(freed)}" : $"减少 {ByteSize.Format(-freed)}，可能有其他程序同时在写入";
        return $"磁盘剩余空间：{ByteSize.Format(result.FreeBytesBefore)} → {ByteSize.Format(result.FreeBytesAfter)}（{change}）";
    }
}
