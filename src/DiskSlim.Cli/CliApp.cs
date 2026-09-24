using System.Diagnostics;
using System.Text;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Cli;

internal static class CliApp
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitCancelled = 130;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var parsed = CliParser.Parse(args);
        if (parsed.Error is { } error)
        {
            stderr.WriteLine(error);
            return ExitUsage;
        }

        var options = parsed.Options!;
        if (options.ShowHelp)
        {
            stdout.Write(CliParser.HelpText);
            return ExitOk;
        }

        if (options.ShowVersion)
        {
            stdout.WriteLine($"diskslim {DiskReportBuilder.ToolVersion}");
            return ExitOk;
        }

        RuleSet rules;
        try
        {
            rules = options.RulesFile is { } file ? RuleLoader.LoadFile(file) : RuleLoader.LoadDefault();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"无法加载规则：{ex.Message}");
            return ExitError;
        }

        var useColor = !options.NoColor && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        var style = new ConsoleStyle(stdout, useColor);
        var width = ConsoleWidth();

        if (options.ListRules)
        {
            RenderRuleList(rules, options.RulesFile ?? "内置规则", style, width);
            return ExitOk;
        }

        if (!new DriveInfo(options.DriveRoot).IsReady)
        {
            stderr.WriteLine($"磁盘 {options.DriveRoot} 不存在或未就绪。");
            return ExitError;
        }

        var reportOptions = new ReportOptions
        {
            DriveRoot = options.DriveRoot,
            TopCount = options.Top,
            Rules = rules,
            IncludeRules = options.IncludeRules,
            IncludeSpaceMap = options.IncludeSpaceMap,
        };

        ScanReport report;
        using (var cts = new CancellationTokenSource())
        {
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                report = BuildWithProgress(reportOptions, stderr, width, cts.Token);
            }
            catch (OperationCanceledException)
            {
                stderr.WriteLine("已取消。");
                return ExitCancelled;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        new ConsoleReportRenderer(style, width, options.Verbose).Render(report);
        return SaveReports(report, options, style, stderr);
    }

    private static ScanReport BuildWithProgress(ReportOptions options, TextWriter stderr, int width, CancellationToken cancellationToken)
    {
        var builder = new DiskReportBuilder();
        var task = Task.Run(() => builder.Build(options, cancellationToken), cancellationToken);
        var showProgress = !Console.IsErrorRedirected;
        var progress = new ProgressDisplay(stderr, width);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(120))
            {
                if (showProgress)
                {
                    progress.Update(DescribeProgress(builder, stopwatch.Elapsed));
                }
            }
        }
        finally
        {
            if (showProgress)
            {
                progress.Clear();
            }
        }

        return task.GetAwaiter().GetResult();
    }

    private static string DescribeProgress(DiskReportBuilder builder, TimeSpan elapsed)
    {
        var text = new StringBuilder(builder.Stage);
        if (builder.MapScanner is { } scanner)
        {
            text.Append($" · {scanner.FilesScanned:N0} 个文件 · {ByteSize.Format(scanner.BytesScanned)}");
        }

        text.Append($" · {elapsed:mm\\:ss}（Ctrl+C 取消）");
        return text.ToString();
    }

    private static int SaveReports(ScanReport report, CliOptions options, ConsoleStyle style, TextWriter stderr)
    {
        try
        {
            if (options.JsonPath is { } jsonPath)
            {
                using var stream = File.Create(jsonPath);
                ReportJson.Write(report, stream);
                style.WriteLine($"JSON 报告已保存：{Path.GetFullPath(jsonPath)}", ConsoleColor.DarkGray);
            }

            if (options.MarkdownPath is { } mdPath)
            {
                using var writer = new StreamWriter(mdPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                MarkdownReportWriter.Write(report, writer);
                style.WriteLine($"Markdown 报告已保存：{Path.GetFullPath(mdPath)}", ConsoleColor.DarkGray);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"保存报告失败：{ex.Message}");
            return ExitError;
        }

        return ExitOk;
    }

    private static void RenderRuleList(RuleSet rules, string source, ConsoleStyle style, int width)
    {
        style.WriteLine($"共 {rules.Rules.Count} 条规则（{source}），按评估顺序列出：", ConsoleColor.White);
        foreach (var rule in rules.Rules)
        {
            style.WriteLine();
            style.Write($"  [{RiskText.Short(rule.Risk)}] ", RiskText.Color(rule.Risk));
            style.Write(rule.Name);
            style.WriteLine($"  {rule.Id} · {rule.Category}" + (rule.MinAgeDays is > 0 ? $" · 只计入 {rule.MinAgeDays} 天前的文件" : ""), ConsoleColor.DarkGray);
            foreach (var path in rule.Paths)
            {
                style.WriteLine("      " + TextWidth.TruncateMiddle(path, Math.Max(20, width - 8)), ConsoleColor.DarkGray);
            }
        }

        style.WriteLine();
    }

    private static int ConsoleWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 120 : Math.Clamp(Console.WindowWidth, 60, 200);
        }
        catch (IOException)
        {
            return 120;
        }
    }
}
