using System.Text;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class RuleLoaderTests
{
    private static RuleSet LoadText(string json) =>
        RuleLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "测试");

    [Fact]
    public void Default_rules_load_and_are_well_formed()
    {
        var set = RuleLoader.LoadDefault();

        Assert.True(set.Rules.Count >= 30);
        Assert.Equal(set.Rules.Count, set.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(set.Rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Advice));
            Assert.NotEmpty(rule.Paths);
            Assert.NotNull(rule.Processes);
        });
    }

    [Fact]
    public void Default_rules_put_broad_user_folders_last()
    {
        var ids = RuleLoader.LoadDefault().Rules.Select(r => r.Id).ToList();

        // 下载文件夹整体规则必须排在安装包规则之后，否则安装包会被重复计算。
        Assert.True(ids.IndexOf("downloads-installers") < ids.IndexOf("downloads"));
        Assert.True(ids.IndexOf("vscode-cache") < ids.IndexOf("electron-web-cache"));
    }

    [Fact]
    public void Optional_fields_get_defaults_and_comments_are_allowed()
    {
        var set = LoadText("""
            {
              // 注释
              "rules": [
                { "id": "a", "name": "A", "paths": ["%TEMP%"], "risk": "high", },
              ],
            }
            """);

        var rule = Assert.Single(set.Rules);
        Assert.Equal(RiskLevel.High, rule.Risk);
        Assert.Empty(rule.Processes);
        Assert.Equal("", rule.Advice);
        Assert.Equal("其他", rule.Category);
        Assert.Null(rule.MinAgeDays);
    }

    [Theory]
    [InlineData("low", RiskLevel.Low)]
    [InlineData("MEDIUM", RiskLevel.Medium)]
    [InlineData("high", RiskLevel.High)]
    public void Parses_risk_levels(string text, RiskLevel expected)
    {
        var set = LoadText($$"""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "{{text}}" } ] }""");

        Assert.Equal(expected, set.Rules[0].Risk);
    }

    [Fact]
    public void Default_rules_never_let_the_tool_clean_high_risk_items()
    {
        var rules = RuleLoader.LoadDefault().Rules;

        Assert.All(rules.Where(r => r.Risk == RiskLevel.High), r => Assert.Equal(CleanupAction.None, r.Action));
        Assert.Equal(CleanupAction.EmptyRecycleBin, rules.Single(r => r.Id == "recycle-bin").Action);
        Assert.Equal(CleanupAction.Recycle, rules.Single(r => r.Id == "downloads-installers").Action);
        Assert.Equal(CleanupAction.PurgeQuarantine, rules.Single(r => r.Id == "quarantine-expired").Action);
        Assert.Contains(rules, r => r.Risk == RiskLevel.Low && r.Action == CleanupAction.Delete);
    }

    [Fact]
    public void Default_rules_never_permanently_delete_items_that_need_confirmation()
    {
        // “需要确认”的内容要能撤销：移入隔离区或移到回收站，不直接永久删除。
        var medium = RuleLoader.LoadDefault().Rules.Where(r => r.Risk == RiskLevel.Medium && r.Action != CleanupAction.None).ToList();

        Assert.NotEmpty(medium);
        Assert.All(medium, r => Assert.Contains(r.Action, new[] { CleanupAction.Quarantine, CleanupAction.Recycle, CleanupAction.EmptyRecycleBin }));
    }

    [Theory]
    [InlineData("delete", CleanupAction.Delete)]
    [InlineData("Recycle", CleanupAction.Recycle)]
    [InlineData("empty-recycle-bin", CleanupAction.EmptyRecycleBin)]
    public void Parses_actions(string text, CleanupAction expected)
    {
        var path = expected == CleanupAction.EmptyRecycleBin ? "::RecycleBin" : "x";
        var set = LoadText($$"""{ "rules": [ { "id": "a", "name": "A", "paths": ["{{path}}"], "risk": "low", "action": "{{text}}" } ] }""");

        Assert.Equal(expected, set.Rules[0].Action);
    }

    [Fact]
    public void Action_defaults_to_none()
    {
        var set = LoadText("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "low" } ] }""");

        Assert.Equal(CleanupAction.None, set.Rules[0].Action);
    }

    [Theory]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "high", "action": "delete" } ] }""", "不能设置 action")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["C:/x"], "risk": "medium", "action": "empty-recycle-bin" } ] }""", "只能是 ::RecycleBin")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["::RecycleBin"], "risk": "medium", "action": "delete" } ] }""", "回收站只能用 empty-recycle-bin")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["C:/x"], "risk": "low", "action": "purge-quarantine" } ] }""", "只能是 ::Quarantine")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["::Quarantine"], "risk": "low", "action": "quarantine" } ] }""", "隔离区只能用 purge-quarantine")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["::RecycleBin"], "risk": "low", "action": "quarantine" } ] }""", "回收站只能用 empty-recycle-bin")]
    public void Rejects_unsafe_actions(string json, string expectedMessage)
    {
        var ex = Assert.Throws<InvalidDataException>(() => LoadText(json));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Writes_risk_levels_in_lower_case()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(RiskLevel.Medium, DiskSlim.Core.Serialization.DiskSlimJsonContext.Default.Options);

        Assert.Equal("\"medium\"", json);
    }

    [Theory]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "low" }, { "id": "A", "name": "B", "paths": ["y"], "risk": "low" } ] }""", "id 重复")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": [], "risk": "low" } ] }""", "paths 不能为空")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "low", "minAgeDays": -1 } ] }""", "minAgeDays")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "", "paths": ["x"], "risk": "low" } ] }""", "缺少 name")]
    public void Rejects_invalid_rules(string json, string expectedMessage)
    {
        var ex = Assert.Throws<InvalidDataException>(() => LoadText(json));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "rules": [ { "id": "a", "paths": ["x"], "risk": "low" } ] }""")] // 缺少 name
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"] } ] }""")] // 缺少 risk：必须明确声明
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "extreme" } ] }""")]
    [InlineData("""{ "rules": [ { "id": "a", "name": "A", "paths": ["x"], "risk": "1" } ] }""")]
    public void Rejects_malformed_json(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => LoadText(json));

        Assert.Contains("格式错误", ex.Message);
    }
}
