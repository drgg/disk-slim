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
