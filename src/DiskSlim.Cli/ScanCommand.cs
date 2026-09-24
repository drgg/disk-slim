using System.Text;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Cli;

/// <summary>diskslim scan：只读扫描并输出报告。</summary>
internal static class ScanCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        var reportOptions = new ReportOptions
        {
            DriveRoot = options.DriveRoot,
            TopCount = options.Top,
            Rules = context.Rules,
            IncludeRules = options.IncludeRules,
            IncludeSpaceMap = options.IncludeSpaceMap,
        };

        var builder = new DiskReportBuilder();
        ScanReport report;
        try
        {
            report = CliApp.RunWithProgress(() => builder.Build(reportOptions, context.CancellationToken), _ => DescribeProgress(builder), context);
        }
        catch (OperationCanceledException)
        {
            context.Stderr.WriteLine("已取消。");
            return CliApp.ExitCancelled;
        }

        new ConsoleReportRenderer(context.Style, context.Width, options.Verbose).Render(report);
        return SaveReports(report, context);
    }

    private static string DescribeProgress(DiskReportBuilder builder)
    {
        var text = new StringBuilder(builder.Stage);
        if (builder.MapScanner is { } scanner)
        {
            text.Append($" · {scanner.FilesScanned:N0} 个文件 · {ByteSize.Format(scanner.BytesScanned)}");
        }

        return text.ToString();
    }

    private static int SaveReports(ScanReport report, CliContext context)
    {
        var options = context.Options;
        try
        {
            if (options.JsonPath is { } jsonPath)
            {
                using var stream = File.Create(jsonPath);
                ReportJson.Write(report, stream);
                context.Style.WriteLine($"JSON 报告已保存：{Path.GetFullPath(jsonPath)}", ConsoleColor.DarkGray);
            }

            if (options.MarkdownPath is { } mdPath)
            {
                using var writer = new StreamWriter(mdPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                MarkdownReportWriter.Write(report, writer);
                context.Style.WriteLine($"Markdown 报告已保存：{Path.GetFullPath(mdPath)}", ConsoleColor.DarkGray);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Stderr.WriteLine($"保存报告失败：{ex.Message}");
            return CliApp.ExitError;
        }

        return CliApp.ExitOk;
    }
}
