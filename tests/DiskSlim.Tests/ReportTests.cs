using System.Text.Json;
using DiskSlim.Cli;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private ScanReport BuildReport()
    {
        _dir.WriteFile("cache/a.bin", 3000);
        _dir.WriteFile("big/movie.mkv", 9000);
        _dir.WriteFile("root.txt", 10);
        var rules = new RuleSet
        {
            Rules =
            [
                new CleanupRule { Id = "test-cache", Name = "测试缓存", Paths = [_dir.PathOf("cache")], Risk = RiskLevel.Low, Advice = "可以删 | 放心" },
                new CleanupRule { Id = "test-missing", Name = "不存在", Paths = [_dir.PathOf("missing")], Risk = RiskLevel.Medium },
            ],
        };

        return new DiskReportBuilder().Build(new ReportOptions
        {
            DriveRoot = Path.GetPathRoot(_dir.Root)!,
            MapRoot = _dir.Root,
            TopCount = 5,
            Rules = rules,
        });
    }

    [Fact]
    public void Builder_combines_rules_and_space_map()
    {
        var report = BuildReport();

        Assert.True(report.RulesChecked);
        Assert.Equal(2, report.RuleCount);
        var rule = Assert.Single(report.Rules); // 没有命中的规则不出现在报告里
        Assert.Equal("test-cache", rule.Rule.Id);
        Assert.Equal(3000, rule.ReclaimableBytes);

        var map = report.SpaceMap!;
        Assert.Equal(12010, map.Stats.Bytes);
        var rootView = map.Folders[0];
        Assert.Equal(_dir.Root, rootView.Path);
        Assert.Equal(["big", "cache", "（本目录中的文件）"], rootView.Items.Select(i => i.Name));
        Assert.True(rootView.Items[2].IsOwnFiles);
        Assert.Equal(_dir.PathOf("big/movie.mkv"), map.LargestFiles[0].Path);
        Assert.Contains(report.Notes, n => n.Contains("不会修改或删除"));
    }

    [Fact]
    public void Json_report_uses_camel_case_and_readable_chinese()
    {
        var json = ReportJson.Serialize(BuildReport());

        using var doc = JsonDocument.Parse(json);
        var rule = doc.RootElement.GetProperty("rules")[0];
        Assert.Equal("low", rule.GetProperty("rule").GetProperty("risk").GetString());
        Assert.Equal(3000, rule.GetProperty("reclaimableBytes").GetInt64());
        Assert.Contains("测试缓存", json);
        Assert.True(doc.RootElement.GetProperty("spaceMap").GetProperty("stats").GetProperty("files").GetInt64() == 3);
    }

    [Fact]
    public void Markdown_report_escapes_table_cells()
    {
        var writer = new StringWriter();
        MarkdownReportWriter.Write(BuildReport(), writer);
        var md = writer.ToString();

        Assert.StartsWith("# DiskSlim 扫描报告", md);
        Assert.Contains("| 测试缓存 | 2.93 KB | 2.93 KB | 1 | 可以删 \\| 放心 |", md);
        Assert.Contains("## 最大的 3 个文件", md);
    }

    [Fact]
    public void Console_renderer_produces_all_sections()
    {
        var writer = new StringWriter();
        new ConsoleReportRenderer(new ConsoleStyle(writer, useColor: false), 200, verbose: true).Render(BuildReport());
        var text = writer.ToString();

        Assert.Contains("■ 磁盘概况", text);
        Assert.Contains("■ 可清理项目", text);
        Assert.Contains("● 可放心清理", text);
        Assert.Contains("■ 空间分布", text);
        Assert.Contains("■ 最大的 3 个文件", text);
        Assert.Contains("■ 扫描统计", text);
        Assert.Contains(_dir.PathOf("cache"), text); // verbose 显示命中路径
    }
}
