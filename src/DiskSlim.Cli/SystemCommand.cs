using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.SystemTasks;

namespace DiskSlim.Cli;

/// <summary>diskslim system：系统级清理（需要管理员权限）。</summary>
internal static class SystemCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        if (options.SystemTaskId is null)
        {
            return List(context);
        }

        if (SystemTaskCatalog.Find(options.SystemTaskId) is not { } task)
        {
            context.Stderr.WriteLine($"没有这项系统任务：{options.SystemTaskId}（运行 diskslim system 查看）。");
            return CliApp.ExitUsage;
        }

        return options.ResultFile is { } resultFile ? RunAsHelper(context, task, resultFile) : RunTask(context, task);
    }

    private static int List(CliContext context)
    {
        var w = context.Style;
        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine($"  ·  系统级清理  ·  管理员权限：{(SystemInfo.IsElevated() ? "是" : "否（运行时会弹出 UAC 提示）")}", ConsoleColor.DarkGray);
        foreach (var task in SystemTaskCatalog.All)
        {
            var estimate = SystemTaskCatalog.Estimate(task);
            w.WriteLine();
            w.Write($"  {task.Name}", ConsoleColor.White);
            w.WriteLine("  " + EstimateText(estimate), estimate.NotNeeded ? ConsoleColor.DarkGray : estimate.NeedsRestart ? ConsoleColor.DarkYellow : ConsoleColor.Green);
            w.WriteLine($"    {task.Description}", ConsoleColor.DarkGray);
            w.WriteLine($"    注意：{task.Warning}", ConsoleColor.DarkYellow);
            if (estimate.CanRun)
            {
                w.WriteLine($"    运行：diskslim system run {task.Id} --execute", ConsoleColor.DarkCyan);
            }
        }

        w.WriteLine();
        return CliApp.ExitOk;
    }

    private static int RunTask(CliContext context, SystemTask task)
    {
        var options = context.Options;
        var w = context.Style;
        var estimate = SystemTaskCatalog.Estimate(task);
        w.WriteLine(task.Name, ConsoleColor.White);
        w.WriteLine($"  {task.Description}");
        w.WriteLine($"  注意：{task.Warning}", ConsoleColor.DarkYellow);
        if (task.UndoHint is { } undo)
        {
            w.WriteLine($"  {undo}", ConsoleColor.DarkGray);
        }

        w.WriteLine($"  预计：{EstimateText(estimate)}", estimate.NeedsRestart ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray);
        if (estimate.NotNeeded)
        {
            return CliApp.ExitOk;
        }

        if (estimate.NeedsRestart)
        {
            w.WriteLine();
            w.WriteLine("现在运行一定会失败，没有做任何改动。请先重启电脑，再运行这项任务。", ConsoleColor.Yellow);
            return options.Execute ? CliApp.ExitError : CliApp.ExitOk;
        }

        if (!options.Execute)
        {
            w.WriteLine();
            w.WriteLine("这只是说明，没有做任何改动。确认后运行：", ConsoleColor.Cyan);
            w.WriteLine($"  diskslim system run {task.Id} --execute", ConsoleColor.White);
            return CliApp.ExitOk;
        }

        if (!options.AssumeYes)
        {
            if (!context.Interactive)
            {
                context.Stderr.WriteLine("需要确认才能执行：请在终端里运行，或加 --yes 跳过确认。");
                return CliApp.ExitOk;
            }

            w.Write(SystemInfo.IsElevated() ? "确定运行吗？输入 y 确认：" : "确定运行吗？确认后会弹出 UAC 提示请求管理员权限。输入 y 确认：", ConsoleColor.Yellow);
            if (context.Stdin.ReadLine()?.Trim() is not ("y" or "Y" or "yes" or "YES" or "Yes" or "是"))
            {
                w.WriteLine("已取消，没有做任何改动。");
                return CliApp.ExitOk;
            }
        }

        var logPath = options.LogPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now);
        SystemTaskResult? result;
        if (SystemInfo.IsElevated())
        {
            result = RunElevated(context, task, logPath);
            if (result is null)
            {
                return CliApp.ExitError;
            }
        }
        else
        {
            // 以管理员身份重新启动自己作为助手，等它完成后读回结果。
            var helper = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序位置");
            result = CliApp.RunWithProgress(
                () => ElevatedHelper.RunAsync(helper, task.Id, logPath).GetAwaiter().GetResult(),
                _ => $"正在以管理员身份运行：{task.Name}（可能需要几分钟）",
                context);
            if (result is null)
            {
                w.WriteLine("你在 UAC 提示里选择了“否”，没有做任何改动。");
                return CliApp.ExitOk;
            }
        }

        RenderResult(context, task, result, logPath);
        return result.Success ? CliApp.ExitOk : CliApp.ExitError;
    }

    /// <summary>助手模式：已经是管理员，被图形界面或普通权限的命令行调用。</summary>
    private static int RunAsHelper(CliContext context, SystemTask task, string resultFile)
    {
        if (!ElevatedHelper.IsValidResultPath(resultFile))
        {
            context.Stderr.WriteLine($"结果文件只能位于 {ElevatedHelper.HelperDirectory}");
            return CliApp.ExitUsage;
        }

        var result = RunElevated(context, task, context.Options.LogPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now))
            ?? new SystemTaskResult { TaskId = task.Id, Success = false, Message = "无法创建操作日志，没有做任何改动。" };
        ElevatedHelper.WriteResult(resultFile, result);
        return result.Success ? CliApp.ExitOk : CliApp.ExitError;
    }

    private static SystemTaskResult? RunElevated(CliContext context, SystemTask task, string logPath)
    {
        JsonLinesOperationLog log;
        try
        {
            log = new JsonLinesOperationLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Stderr.WriteLine($"无法创建操作日志 {logPath}：{ex.Message}。为安全起见，没有做任何改动。");
            return null;
        }

        using (log)
        {
            var runner = new SystemTaskRunner(log: log);
            return CliApp.RunWithProgress(
                () => runner.RunAsync(task.Id).GetAwaiter().GetResult(),
                _ => $"正在运行：{task.Name}（可能需要几分钟，请不要关机）",
                context);
        }
    }

    private static void RenderResult(CliContext context, SystemTask task, SystemTaskResult result, string logPath)
    {
        var w = context.Style;
        w.WriteLine();
        w.WriteLine((result.Success ? "✓ " : "✗ ") + $"{task.Name}：{result.Message}", result.Success ? ConsoleColor.Green : ConsoleColor.Red);
        if (result.FreeBytesAfter > 0)
        {
            var freed = result.FreeBytesAfter - result.FreeBytesBefore;
            w.WriteLine($"  磁盘剩余空间：{ByteSize.Format(result.FreeBytesBefore)} → {ByteSize.Format(result.FreeBytesAfter)}（{(freed >= 0 ? "增加" : "减少")} {ByteSize.Format(Math.Abs(freed))}），用时 {result.ElapsedSeconds:0} 秒");
        }

        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            var lines = result.Output.Split(Environment.NewLine);
            foreach (var line in context.Options.Verbose ? lines : lines.TakeLast(6))
            {
                w.WriteLine("    " + line, ConsoleColor.DarkGray);
            }
        }

        w.WriteLine($"  操作日志：{logPath}", ConsoleColor.DarkGray);
    }

    internal static string EstimateText(SystemTaskEstimate estimate) =>
        estimate.NotNeeded ? estimate.Note
        : estimate.Bytes is { } bytes ? $"约可释放 {ByteSize.Format(bytes)}" + (estimate.Note.Length > 0 ? $"（{estimate.Note}）" : "")
        : estimate.Note;
}
