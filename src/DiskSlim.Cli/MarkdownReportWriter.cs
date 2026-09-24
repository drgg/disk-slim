using System.Globalization;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Cli;

/// <summary>把扫描报告写成 Markdown。</summary>
internal static class MarkdownReportWriter
{
    public static void Write(ScanReport report, TextWriter md)
    {
        var drive = report.Drive;
        md.WriteLine("# DiskSlim 扫描报告");
        md.WriteLine();
        md.WriteLine($"- 生成时间：{report.GeneratedAt:yyyy-MM-dd HH:mm}");
        md.WriteLine($"- 版本：{report.ToolVersion}，管理员权限：{(report.IsElevated ? "是" : "否")}");
        md.WriteLine($"- 磁盘：`{drive.Root}`（{drive.Format}），总容量 {ByteSize.Format(drive.TotalBytes)}，"
            + $"已用 {ByteSize.Format(drive.UsedBytes)}（{ConsoleReportRenderer.Percent(drive.UsedRatio)}），剩余 {ByteSize.Format(drive.FreeBytes)}");

        if (report.RulesChecked)
        {
            WriteRules(report, md);
        }

        if (report.SpaceMap is { } map)
        {
            WriteSpaceMap(map, md);
        }

        if (report.Notes.Count > 0)
        {
            md.WriteLine();
            md.WriteLine("## 说明");
            md.WriteLine();
            foreach (var note in report.Notes)
            {
                md.WriteLine($"- {Escape(note)}");
            }
        }
    }

    private static void WriteRules(ScanReport report, TextWriter md)
    {
        md.WriteLine();
        md.WriteLine("## 可清理项目");
        md.WriteLine();
        md.WriteLine($"检查了 {report.RuleCount} 条规则，发现 {report.Rules.Count} 项。");

        foreach (var risk in RiskText.Order)
        {
            var items = report.Rules.Where(r => r.Rule.Risk == risk).OrderByDescending(RiskText.CountedBytes).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var sum = items.Sum(RiskText.CountedBytes);
            md.WriteLine();
            md.WriteLine($"### {RiskText.Title(risk)}（{(risk == RiskLevel.High ? "合计" : "合计可释放")} {ByteSize.Format(sum)}）");
            md.WriteLine();
            md.WriteLine("| 项目 | 可释放 | 总大小 | 文件数 | 建议 |");
            md.WriteLine("|---|---:|---:|---:|---|");
            foreach (var item in items)
            {
                var notes = new List<string> { item.Rule.Advice };
                if (item.RunningProcesses.Count > 0)
                {
                    notes.Add($"⚠ {string.Join("、", item.RunningProcesses)} 正在运行");
                }

                if (item.InaccessibleDirectories > 0)
                {
                    notes.Add($"⚠ {item.InaccessibleDirectories:N0} 个文件夹无权限读取");
                }

                var reclaimable = risk == RiskLevel.High ? "-" : ByteSize.Format(item.ReclaimableBytes);
                md.WriteLine($"| {Escape(item.Rule.Name)} | {reclaimable} | {ByteSize.Format(item.TotalBytes)} | "
                    + $"{item.FileCount.ToString("N0", CultureInfo.InvariantCulture)} | {Escape(string.Join("<br>", notes))} |");
            }
        }
    }

    private static void WriteSpaceMap(SpaceMap map, TextWriter md)
    {
        md.WriteLine();
        md.WriteLine("## 空间分布");
        foreach (var view in map.Folders)
        {
            md.WriteLine();
            md.WriteLine($"### {view.Title} `{view.Path}`（{ByteSize.Format(view.TotalBytes)}）");
            md.WriteLine();
            md.WriteLine("| 大小 | 占比 | 名称 |");
            md.WriteLine("|---:|---:|---|");
            foreach (var item in view.Items)
            {
                var ratio = view.TotalBytes == 0 ? 0 : (double)item.Bytes / view.TotalBytes;
                var name = Escape(item.Name) + (item.Incomplete ? " \\*" : "");
                md.WriteLine($"| {ByteSize.Format(item.Bytes)} | {ConsoleReportRenderer.Percent(ratio)} | {name} |");
            }
        }

        if (map.LargestFiles.Count > 0)
        {
            md.WriteLine();
            md.WriteLine($"## 最大的 {map.LargestFiles.Count} 个文件");
            md.WriteLine();
            md.WriteLine("| 大小 | 修改日期 | 路径 |");
            md.WriteLine("|---:|---|---|");
            foreach (var file in map.LargestFiles)
            {
                md.WriteLine($"| {ByteSize.Format(file.Bytes)} | {file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd} | `{file.Path}` |");
            }
        }

        var s = map.Stats;
        md.WriteLine();
        md.WriteLine("## 扫描统计");
        md.WriteLine();
        md.WriteLine($"- 共 {s.Files:N0} 个文件、{s.Directories:N0} 个文件夹，合计 {ByteSize.Format(s.Bytes)}，用时 {map.ElapsedSeconds:0.0} 秒");
        md.WriteLine($"- 无法读取 {s.InaccessibleDirectories:N0} 个文件夹，跳过 {s.SkippedLinks:N0} 个目录链接，{s.CloudOnlyFiles + s.CloudOnlyDirectories:N0} 个云盘仅在线项目未计入");
        if (map.Errors.Count > 0)
        {
            md.WriteLine();
            md.WriteLine("<details><summary>无法读取的文件夹</summary>");
            md.WriteLine();
            foreach (var error in map.Errors)
            {
                md.WriteLine($"- `{error.Path}`：{Escape(error.Message)}");
            }

            md.WriteLine();
            md.WriteLine("</details>");
        }
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
