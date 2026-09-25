using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Duplicates;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Reporting;

namespace DiskSlim.Cli;

/// <summary>diskslim dupes：查找重复文件；加 --remove 把建议的副本移入隔离区。</summary>
internal static class DupesCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var roots = options.DupesRoots.Count > 0
            ? options.DupesRoots
            : [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
        var missing = roots.Where(r => !Directory.Exists(r)).ToList();
        if (missing.Count > 0)
        {
            context.Stderr.WriteLine($"目录不存在：{string.Join("、", missing)}");
            return CliApp.ExitUsage;
        }

        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine("  ·  查找重复文件（只读取，不修改）", ConsoleColor.DarkGray);
        w.WriteLine($"{string.Join("、", roots)} · 不小于 {ByteSize.Format(options.MinFileSize)} 的文件", ConsoleColor.DarkGray);

        var finder = new DuplicateFinder();
        var searchOptions = DuplicateSearchOptions.ForCurrentUser(context.Rules, options.MinFileSize);
        var guard = searchOptions.Guard!;
        w.WriteLine("不查软件数据（AppData、ProgramData）和开发工具目录（.git、node_modules……）", ConsoleColor.DarkGray);

        DuplicateSearchResult result;
        try
        {
            result = CliApp.RunWithProgress(
                () => finder.Find(roots, searchOptions, context.CancellationToken),
                _ => $"{finder.Stage} · {finder.FilesScanned:N0} 个文件 · 已读取 {ByteSize.Format(finder.BytesHashed)}",
                context);
        }
        catch (OperationCanceledException)
        {
            context.Stderr.WriteLine("已取消。");
            return CliApp.ExitCancelled;
        }

        RenderGroups(result, context);
        if (!options.RemoveDuplicates)
        {
            if (result.Groups.Any(g => g.SuggestedRemovals.Count > 0))
            {
                w.WriteLine("  把建议移除的副本移入隔离区（可以恢复）：", ConsoleColor.DarkGray);
                w.WriteLine("    " + CommandLine(roots, options, "--remove"), ConsoleColor.White);
                w.WriteLine();
            }

            return CliApp.ExitOk;
        }

        return Remove(result, roots, guard, context);
    }

    private static void RenderGroups(DuplicateSearchResult result, CliContext context)
    {
        var w = context.Style;
        var width = context.Width;
        w.WriteLine();
        if (result.Groups.Count == 0)
        {
            w.WriteLine($"  没有找到重复文件（查了 {result.FilesScanned:N0} 个文件，用时 {result.ElapsedSeconds:0.0} 秒）。");
            w.WriteLine();
            return;
        }

        w.Write($"■ 找到 {result.Groups.Count:N0} 组重复文件，只保留一份可以省下 {ByteSize.Format(result.WastedBytes)}", ConsoleColor.Cyan);
        w.WriteLine($"  查了 {result.FilesScanned:N0} 个文件，用时 {result.ElapsedSeconds:0.0} 秒", ConsoleColor.DarkGray);
        if (result.HardLinksIgnored > 0 || result.UnreadableFiles > 0)
        {
            w.WriteLine($"  {result.HardLinksIgnored:N0} 个硬链接不算重复（删掉也不省空间）· {result.UnreadableFiles:N0} 个文件读不了（通常正被占用）", ConsoleColor.DarkGray);
        }

        var shown = result.Groups.Take(context.Options.Top).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var group = shown[i];
            w.WriteLine();
            w.WriteLine($"  {i + 1}. {ByteSize.Format(group.FileSize)} × {group.Files.Count} 份（可省 {ByteSize.Format(group.WastedBytes)}）", ConsoleColor.White);
            var keeper = group.SuggestedKeeper;
            var removals = group.SuggestedRemovals;
            const int maxFilesShown = 8;

            // 先列建议保留的那份，再列其他的。
            var ordered = group.Files.OrderBy(f => ReferenceEquals(f, keeper) ? 0 : 1).ToList();
            foreach (var file in ordered.Take(maxFilesShown))
            {
                var (tag, color) = file.IsProtected ? ("受保护", ConsoleColor.Red)
                    : removals.Contains(file) ? ("可移除", ConsoleColor.DarkYellow)
                    : ReferenceEquals(file, keeper) ? ("保留", ConsoleColor.Green)
                    : ("重复", ConsoleColor.DarkGray);
                w.Write($"     [{tag}] ", color);
                w.Write($"{file.CreationTimeUtc.ToLocalTime():yyyy-MM-dd}  ", ConsoleColor.DarkGray);
                w.WriteLine(TextWidth.TruncateMiddle(file.Path, Math.Max(20, width - 26)));
                if (file.ProtectedReason is { } reason)
                {
                    w.WriteLine($"              {reason}", ConsoleColor.DarkGray);
                }
            }

            if (ordered.Count > maxFilesShown)
            {
                w.WriteLine($"     …… 还有 {ordered.Count - maxFilesShown:N0} 份", ConsoleColor.DarkGray);
            }

            if (group.HasProtectedFiles)
            {
                w.WriteLine("     组里有受保护的文件，不给移除建议；需要的话请在对应软件里处理。", ConsoleColor.DarkGray);
            }
        }

        if (result.Groups.Count > shown.Count)
        {
            w.WriteLine();
            w.WriteLine($"  还有 {result.Groups.Count - shown.Count:N0} 组没有列出（用 --top 显示更多）。", ConsoleColor.DarkGray);
        }

        w.WriteLine();
        w.WriteLine("  [可移除] 只标在明显的副本上（文件名带“(1)”“副本”“- Copy”，或在下载文件夹里）；", ConsoleColor.DarkGray);
        w.WriteLine("  [重复] 的文件内容也一样，但可能被项目或软件引用，请自己判断（可以在图形界面里勾选）。", ConsoleColor.DarkGray);
        w.WriteLine();
    }

    private static int Remove(DuplicateSearchResult result, IReadOnlyList<string> roots, SafetyGuard guard, CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var selections = result.Groups
            .Where(g => g.SuggestedRemovals.Count > 0)
            .Select(g => new DuplicateSelection(g, g.SuggestedRemovals))
            .ToList();
        if (selections.Count == 0)
        {
            w.WriteLine("  没有可以移除的副本。");
            return CliApp.ExitOk;
        }

        // 同一块盘上的隔离区才能瞬间移入；每块盘分别处理。
        var byDrive = selections.GroupBy(s => Path.GetPathRoot(s.Group.Files[0].Path)!, StringComparer.OrdinalIgnoreCase).ToList();
        var preview = byDrive.Select(d => new DuplicateRemover(QuarantineStore.ForDrive(d.Key), guard).Remove(d.ToList(), dryRun: true)).ToList();
        var files = preview.Sum(p => p.Files);
        var bytes = preview.Sum(p => p.Bytes);
        w.WriteLine($"■ 将把 {files:N0} 个副本移入隔离区（{ByteSize.Format(bytes)}），每组保留一份。", ConsoleColor.Cyan);
        w.WriteLine("  隔离区里的文件可以随时恢复；确认没问题后彻底删除才会释放空间。", ConsoleColor.DarkGray);
        foreach (var skipped in preview.SelectMany(p => p.Skipped).Take(10))
        {
            w.WriteLine($"  跳过 {TextWidth.TruncateMiddle(skipped.Path, Math.Max(20, context.Width - 40))}：{skipped.Reason}", ConsoleColor.DarkYellow);
        }

        if (!options.Execute)
        {
            w.WriteLine();
            w.WriteLine("  这是模拟运行，没有移动任何文件。确认无误后运行：", ConsoleColor.Cyan);
            w.WriteLine("    " + CommandLine(roots, options, "--remove --execute"), ConsoleColor.White);
            w.WriteLine();
            return CliApp.ExitOk;
        }

        if (!options.AssumeYes)
        {
            if (!context.Interactive)
            {
                context.Stderr.WriteLine("需要确认才能执行：请在终端里运行，或加 --yes 跳过确认。");
                return CliApp.ExitOk;
            }

            w.Write("  确定移入隔离区吗？输入 y 确认，其他任意键取消：", ConsoleColor.Yellow);
            if (context.Stdin.ReadLine()?.Trim() is not ("y" or "Y" or "yes" or "YES" or "Yes" or "是"))
            {
                w.WriteLine("  已取消，没有移动任何文件。");
                return CliApp.ExitOk;
            }
        }

        var logPath = options.LogPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now);
        JsonLinesOperationLog log;
        try
        {
            log = new JsonLinesOperationLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Stderr.WriteLine($"无法创建操作日志 {logPath}：{ex.Message}。为安全起见，没有移动任何文件。");
            return CliApp.ExitError;
        }

        var results = new List<DuplicateRemovalResult>();
        using (log)
        {
            foreach (var drive in byDrive)
            {
                results.Add(new DuplicateRemover(QuarantineStore.ForDrive(drive.Key), guard, log).Remove(drive.ToList(), dryRun: false, context.CancellationToken));
            }
        }

        w.WriteLine();
        w.WriteLine($"  已把 {results.Sum(r => r.Files):N0} 个副本（{ByteSize.Format(results.Sum(r => r.Bytes))}）移入隔离区。", ConsoleColor.Green);
        foreach (var skipped in results.SelectMany(r => r.Skipped).Take(10))
        {
            w.WriteLine($"  跳过 {TextWidth.TruncateMiddle(skipped.Path, Math.Max(20, context.Width - 40))}：{skipped.Reason}", ConsoleColor.DarkYellow);
        }

        foreach (var sessionId in results.Select(r => r.QuarantineSessionId).OfType<string>())
        {
            w.WriteLine($"  撤销：diskslim quarantine restore {sessionId}", ConsoleColor.Cyan);
            w.WriteLine($"  确认没问题后释放空间：diskslim quarantine purge {sessionId} --execute", ConsoleColor.DarkGray);
        }

        w.WriteLine($"  操作日志：{log.FilePath}", ConsoleColor.DarkGray);
        return CliApp.ExitOk;
    }

    private static string CommandLine(IReadOnlyList<string> roots, CliOptions options, string extra)
    {
        var parts = new List<string> { "diskslim dupes" };
        parts.AddRange(options.DupesRoots.Select(r => r.Contains(' ') ? $"\"{r}\"" : r));
        if (options.MinFileSize != 1024 * 1024)
        {
            parts.Add($"--min-size {options.MinFileSize}");
        }

        parts.Add(extra);
        return string.Join(" ", parts);
    }
}
