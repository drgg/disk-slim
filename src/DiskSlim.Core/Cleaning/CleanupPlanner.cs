using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Cleaning;

public enum PlanStatus
{
    /// <summary>可以执行。</summary>
    Ready,

    /// <summary>没有找到要清理的内容。</summary>
    NothingFound,

    /// <summary>相关程序正在运行，跳过。</summary>
    ProcessRunning,

    /// <summary>目标触及受保护的位置，整条规则拒绝执行。</summary>
    Forbidden,
}

public sealed record PlannedRule
{
    public required CleanupRule Rule { get; init; }

    public required PlanStatus Status { get; init; }

    /// <summary>要清理的路径（清空回收站时为空）。</summary>
    public IReadOnlyList<MatchedTarget> Targets { get; init; } = [];

    public IReadOnlyList<string> RunningProcesses { get; init; } = [];

    /// <summary>被拒绝的原因。</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>是否由用户点名选中（否则是默认选中的“可放心清理”项目）。</summary>
    public bool ExplicitlySelected { get; init; }
}

public sealed record CleanupPlan
{
    public required string DriveRoot { get; init; }

    /// <summary>选中的规则，按规则文件中的顺序。</summary>
    public required IReadOnlyList<PlannedRule> Rules { get; init; }

    /// <summary>有清理方式但这次没有选中的规则，可以用 --rule 点名。</summary>
    public required IReadOnlyList<CleanupRule> Available { get; init; }
}

/// <summary>选择的规则不合法（不存在、仅供参考、没有清理方式）。</summary>
public sealed class CleanupSelectionException(string message) : Exception(message);

/// <summary>根据规则和用户的选择生成清理计划。只读取，不删除。</summary>
public sealed class CleanupPlanner(SafetyGuard guard, IReadOnlySet<string> runningProcesses)
{
    /// <param name="ruleIds">点名的规则；为空时选中所有“可放心清理”并且有清理方式的规则。</param>
    /// <exception cref="CleanupSelectionException">点名的规则不存在或不能清理。</exception>
    public CleanupPlan Plan(RuleSet rules, string driveRoot, IReadOnlyCollection<string> ruleIds)
    {
        var selected = Select(rules, ruleIds);

        // 按顺序匹配全部规则（包括没有选中的），这样路径归属和“查看报告”时完全一致，
        // 选中的规则永远不会清理到归属于其他规则（包括“仅供参考”的规则）的路径。
        var matcher = new RuleMatcher(driveRoot);
        var planned = new List<PlannedRule>();
        foreach (var rule in rules.Rules)
        {
            var targets = matcher.Match(rule);
            if (selected.Contains(rule))
            {
                planned.Add(PlanRule(rule, targets, driveRoot, ruleIds.Count > 0));
            }
        }

        var available = rules.Rules
            .Where(r => r.Action != CleanupAction.None && r.Risk != RiskLevel.High && !selected.Contains(r))
            .ToList();

        return new CleanupPlan { DriveRoot = driveRoot, Rules = planned, Available = available };
    }

    private static HashSet<CleanupRule> Select(RuleSet rules, IReadOnlyCollection<string> ruleIds)
    {
        if (ruleIds.Count == 0)
        {
            return rules.Rules.Where(r => r.Risk == RiskLevel.Low && r.Action != CleanupAction.None).ToHashSet();
        }

        var byId = rules.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<CleanupRule>();
        var problems = new List<string>();
        foreach (var id in ruleIds)
        {
            if (!byId.TryGetValue(id, out var rule))
            {
                problems.Add($"没有 id 为 {id} 的规则（运行 diskslim --list-rules 查看全部规则）");
            }
            else if (rule.Risk == RiskLevel.High)
            {
                problems.Add($"{rule.Id}（{rule.Name}）是“仅供参考”的项目，工具不会清理它。{rule.Advice}");
            }
            else if (rule.Action == CleanupAction.None)
            {
                problems.Add($"{rule.Id}（{rule.Name}）没有自动清理方式，请手动处理：{rule.Advice}");
            }
            else
            {
                selected.Add(rule);
            }
        }

        if (problems.Count > 0)
        {
            throw new CleanupSelectionException(string.Join(Environment.NewLine, problems));
        }

        return selected;
    }

    private PlannedRule PlanRule(CleanupRule rule, IReadOnlyList<MatchedTarget> targets, string driveRoot, bool explicitlySelected)
    {
        var running = RuleEvaluator.RunningOf(rule, runningProcesses);
        if (running.Count > 0)
        {
            return new PlannedRule { Rule = rule, Status = PlanStatus.ProcessRunning, Targets = targets, RunningProcesses = running, ExplicitlySelected = explicitlySelected };
        }

        if (rule.Action == CleanupAction.EmptyRecycleBin)
        {
            var bin = RecycleBin.Query(driveRoot);
            var status = bin is { ItemCount: > 0 } ? PlanStatus.Ready : PlanStatus.NothingFound;
            return new PlannedRule { Rule = rule, Status = status, ExplicitlySelected = explicitlySelected };
        }

        var problems = new List<string>();
        foreach (var target in targets)
        {
            if (guard.Check(target.Path) is { } reason)
            {
                problems.Add(reason);
            }
            else if (LinkTargetOf(target) is { } linkTarget)
            {
                problems.Add($"{target.Path} 是指向 {linkTarget} 的链接，为安全起见不清理");
            }
        }

        if (problems.Count > 0)
        {
            return new PlannedRule { Rule = rule, Status = PlanStatus.Forbidden, Targets = targets, Problems = problems, ExplicitlySelected = explicitlySelected };
        }

        return new PlannedRule
        {
            Rule = rule,
            Status = targets.Count > 0 ? PlanStatus.Ready : PlanStatus.NothingFound,
            Targets = targets,
            ExplicitlySelected = explicitlySelected,
        };
    }

    private static string? LinkTargetOf(MatchedTarget target)
    {
        try
        {
            FileSystemInfo info = target.IsFile ? new FileInfo(target.Path) : new DirectoryInfo(target.Path);
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 ? info.LinkTarget : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
