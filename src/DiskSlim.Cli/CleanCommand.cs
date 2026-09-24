using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;

namespace DiskSlim.Cli;

/// <summary>
/// diskslim clean：先模拟，展示计划；加 --execute 并确认后才真正执行，每一步都写入操作日志。
/// </summary>
internal static class CleanCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        var renderer = new CleanRenderer(context.Style, context.Width, options.Verbose);

        CleanupPlan plan;
        try
        {
            plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), SystemInfo.RunningProcessNames())
                .Plan(context.Rules, options.DriveRoot, options.RuleIds);
        }
        catch (CleanupSelectionException ex)
        {
            context.Stderr.WriteLine(ex.Message);
            return CliApp.ExitUsage;
        }

        renderer.RenderHeader(options.Execute, options.DriveRoot, SystemInfo.IsElevated());

        // 第一步：模拟。和真正执行走同一套判断，只是不做任何写操作。
        var simulator = new CleanupExecutor();
        var preview = CliApp.RunWithProgress(
            () => simulator.Execute(plan, dryRun: true, context.CancellationToken),
            _ => $"正在检查：{simulator.CurrentRule} · {simulator.FilesVisited:N0} 个文件",
            context);
        if (preview.Cancelled)
        {
            context.Stderr.WriteLine("已取消，没有删除任何文件。");
            return CliApp.ExitCancelled;
        }

        renderer.RenderPlan(plan, preview);
        if (!options.Execute)
        {
            renderer.RenderDryRunFooter(plan, options);
            return CliApp.ExitOk;
        }

        if (preview.Rules.Sum(r => r.Files) == 0)
        {
            context.Style.WriteLine();
            context.Style.WriteLine("  没有需要清理的内容。");
            return CliApp.ExitOk;
        }

        // 第二步：确认。
        if (!options.AssumeYes && !Confirm(context, renderer, preview))
        {
            return CliApp.ExitOk;
        }

        // 第三步：执行，并记录日志。没有日志就不执行。
        JsonLinesOperationLog log;
        var logPath = options.LogPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now);
        try
        {
            log = new JsonLinesOperationLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Stderr.WriteLine($"无法创建操作日志 {logPath}：{ex.Message}。为安全起见，没有执行清理。");
            return CliApp.ExitError;
        }

        CleanupResult result;
        using (log)
        {
            var executor = new CleanupExecutor(log: log);
            result = CliApp.RunWithProgress(
                () => executor.Execute(plan, dryRun: false, context.CancellationToken),
                _ => $"正在清理：{executor.CurrentRule} · 已释放 {ByteSize.Format(executor.BytesCleaned)}",
                context);
        }

        renderer.RenderResult(result);
        return result.Cancelled ? CliApp.ExitCancelled : CliApp.ExitOk;
    }

    private static bool Confirm(CliContext context, CleanRenderer renderer, CleanupResult preview)
    {
        if (!context.Interactive)
        {
            context.Stderr.WriteLine("需要确认才能执行清理：请在终端里运行，或加 --yes 跳过确认。");
            return false;
        }

        context.Style.WriteLine();
        context.Style.Write(renderer.ConfirmPrompt(preview), ConsoleColor.Yellow);
        var answer = context.Stdin.ReadLine()?.Trim();
        if (answer is "y" or "Y" or "yes" or "YES" or "Yes" or "是")
        {
            return true;
        }

        context.Style.WriteLine("已取消，没有删除任何文件。");
        return false;
    }
}
