using System.Diagnostics;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Cli;

/// <summary>命令行运行时需要的输入输出和共享设置。</summary>
internal sealed record CliContext(
    CliOptions Options,
    RuleSet Rules,
    ConsoleStyle Style,
    TextWriter Stderr,
    TextReader Stdin,
    bool Interactive,
    int Width,
    CancellationToken CancellationToken);

internal static class CliApp
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;
    public const int ExitCancelled = 130;

    /// <param name="interactive">能否向用户提问确认；为 null 时根据标准输入是否被重定向判断。</param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin, bool? interactive = null)
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

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // 不直接结束进程：让清理把当前文件处理完、写好日志再停下。
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var context = new CliContext(options, rules, style, stderr, stdin, interactive ?? !Console.IsInputRedirected, width, cts.Token);
            return options.Command switch
            {
                CliCommand.Clean => CleanCommand.Run(context),
                CliCommand.Quarantine => QuarantineCommand.Run(context),
                _ => ScanCommand.Run(context),
            };
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>在后台执行 <paramref name="work"/>，同时在标准错误上刷新进度。</summary>
    internal static T RunWithProgress<T>(Func<T> work, Func<TimeSpan, string> describe, CliContext context)
    {
        var task = Task.Run(work);
        var showProgress = !Console.IsErrorRedirected;
        var progress = new ProgressDisplay(context.Stderr, context.Width);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(120))
            {
                if (showProgress)
                {
                    progress.Update(describe(stopwatch.Elapsed) + $" · {stopwatch.Elapsed:mm\\:ss}（Ctrl+C 取消）");
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

    private static void RenderRuleList(RuleSet rules, string source, ConsoleStyle style, int width)
    {
        style.WriteLine($"共 {rules.Rules.Count} 条规则（{source}），按评估顺序列出：", ConsoleColor.White);
        foreach (var rule in rules.Rules)
        {
            style.WriteLine();
            style.Write($"  [{RiskText.Short(rule.Risk)}] ", RiskText.Color(rule.Risk));
            style.Write(rule.Name);
            var details = new List<string> { rule.Id, rule.Category, RiskText.Action(rule.Action) };
            if (rule.MinAgeDays is > 0)
            {
                details.Add($"只处理 {rule.MinAgeDays} 天前的文件");
            }

            style.WriteLine("  " + string.Join(" · ", details), ConsoleColor.DarkGray);
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
