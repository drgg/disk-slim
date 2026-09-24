using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class CleanupPlannerTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly string _drive;

    public CleanupPlannerTests() => _drive = Path.GetPathRoot(_dir.Root)!;

    public void Dispose() => _dir.Dispose();

    private static CleanupRule Rule(string id, RiskLevel risk, CleanupAction action, params string[] paths) => new()
    {
        Id = id,
        Name = id,
        Risk = risk,
        Action = action,
        Paths = paths,
    };

    private CleanupPlan Plan(RuleSet rules, IReadOnlyCollection<string>? ids = null, IReadOnlySet<string>? running = null) =>
        new CleanupPlanner(SafetyGuard.ForCurrentUser(), running ?? new HashSet<string>())
            .Plan(rules, _drive, ids ?? []);

    [Fact]
    public void Default_selection_is_low_risk_rules_with_an_action()
    {
        _dir.WriteFile("a/x.tmp", 1);
        _dir.WriteFile("b/x.tmp", 1);
        _dir.WriteFile("c/x.tmp", 1);
        var rules = new RuleSet
        {
            Rules =
            [
                Rule("low-delete", RiskLevel.Low, CleanupAction.Delete, _dir.PathOf("a")),
                Rule("low-advice-only", RiskLevel.Low, CleanupAction.None, _dir.PathOf("b")),
                Rule("medium-delete", RiskLevel.Medium, CleanupAction.Delete, _dir.PathOf("c")),
            ],
        };

        var plan = Plan(rules);

        var planned = Assert.Single(plan.Rules);
        Assert.Equal("low-delete", planned.Rule.Id);
        Assert.Equal(PlanStatus.Ready, planned.Status);
        Assert.False(planned.ExplicitlySelected);
        Assert.Equal(["medium-delete"], plan.Available.Select(r => r.Id));
    }

    [Fact]
    public void Medium_rules_can_be_selected_by_id()
    {
        _dir.WriteFile("c/x.tmp", 1);
        var rules = new RuleSet { Rules = [Rule("medium-delete", RiskLevel.Medium, CleanupAction.Delete, _dir.PathOf("c"))] };

        var plan = Plan(rules, ["MEDIUM-DELETE"]);

        Assert.Equal(PlanStatus.Ready, Assert.Single(plan.Rules).Status);
        Assert.True(plan.Rules[0].ExplicitlySelected);
    }

    [Theory]
    [InlineData("high-rule", "仅供参考")]
    [InlineData("advice-only", "没有自动清理方式")]
    [InlineData("no-such-rule", "没有 id 为 no-such-rule 的规则")]
    public void Rejects_rules_that_cannot_be_cleaned(string id, string expectedMessage)
    {
        var rules = new RuleSet
        {
            Rules =
            [
                Rule("high-rule", RiskLevel.High, CleanupAction.None, _dir.PathOf("h")),
                Rule("advice-only", RiskLevel.Medium, CleanupAction.None, _dir.PathOf("m")),
            ],
        };

        var ex = Assert.Throws<CleanupSelectionException>(() => Plan(rules, [id]));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Skips_rules_whose_programs_are_running()
    {
        _dir.WriteFile("cache/x", 1);
        var rule = Rule("app-cache", RiskLevel.Low, CleanupAction.Delete, _dir.PathOf("cache")) with { Processes = ["CloudMusic.exe"] };

        var plan = Plan(new RuleSet { Rules = [rule] }, running: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cloudmusic" });

        var planned = Assert.Single(plan.Rules);
        Assert.Equal(PlanStatus.ProcessRunning, planned.Status);
        Assert.Equal(["cloudmusic"], planned.RunningProcesses.Select(p => p.ToLowerInvariant()));
    }

    [Fact]
    public void Refuses_rules_that_target_protected_folders()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var rule = Rule("evil", RiskLevel.Low, CleanupAction.Delete, documents);

        var planned = Assert.Single(Plan(new RuleSet { Rules = [rule] }).Rules);

        Assert.Equal(PlanStatus.Forbidden, planned.Status);
        Assert.NotEmpty(planned.Problems);
    }

    [Fact]
    public void Refuses_targets_that_are_links()
    {
        using var elsewhere = new TestDirectory();
        elsewhere.WriteFile("precious.doc", 10);
        _dir.CreateJunction("cache", elsewhere.Root);
        var rule = Rule("linked-cache", RiskLevel.Low, CleanupAction.Delete, _dir.PathOf("cache"));

        var planned = Assert.Single(Plan(new RuleSet { Rules = [rule] }).Rules);

        Assert.Equal(PlanStatus.Forbidden, planned.Status);
        Assert.Contains("链接", planned.Problems[0]);
    }

    [Fact]
    public void Paths_owned_by_other_rules_become_exclusions_even_if_those_rules_are_not_selected()
    {
        _dir.WriteFile("appdata/chat/history.db", 100);
        _dir.WriteFile("appdata/cache.tmp", 10);
        var rules = new RuleSet
        {
            Rules =
            [
                Rule("chat-history", RiskLevel.High, CleanupAction.None, _dir.PathOf("appdata/chat")),
                Rule("appdata-temp", RiskLevel.Low, CleanupAction.Delete, _dir.PathOf("appdata")),
            ],
        };

        var planned = Assert.Single(Plan(rules).Rules);

        var target = Assert.Single(planned.Targets);
        Assert.Equal([_dir.PathOf("appdata/chat")], target.Exclusions.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_paths_mean_nothing_found()
    {
        var rule = Rule("gone", RiskLevel.Low, CleanupAction.Delete, _dir.PathOf("missing"));

        Assert.Equal(PlanStatus.NothingFound, Assert.Single(Plan(new RuleSet { Rules = [rule] }).Rules).Status);
    }
}
