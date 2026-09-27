using DiskSlim.Core.Archives;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Reporting;

namespace DiskSlim.Cli;

/// <summary>diskslim archives：找出已经解压过的压缩包；加 --remove 把逐个文件核对过的压缩包移入隔离区。</summary>
internal static class ArchivesCommand
{
    public static int Run(CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var minSize = options.MinSize ?? ArchiveSearchOptions.DefaultMinSize;
        var roots = options.DupesRoots.Count > 0 ? options.DupesRoots : ArchiveSearchOptions.DefaultRoots();
        var missing = roots.Where(r => !Directory.Exists(r)).ToList();
        if (missing.Count > 0)
        {
            context.Stderr.WriteLine($"目录不存在：{string.Join("、", missing)}");
            return CliApp.ExitUsage;
        }

        w.Write($"DiskSlim 硬盘瘦身 v{DiskReportBuilder.ToolVersion}", ConsoleColor.White);
        w.WriteLine("  ·  已经解压过的压缩包（只读取，不修改）", ConsoleColor.DarkGray);
        if (roots.Count == 0)
        {
            w.WriteLine();
            w.WriteLine("  没有找到默认的下载文件夹。请指定要查找的位置，例如 diskslim archives D:\\下载");
            w.WriteLine();
            return CliApp.ExitOk;
        }

        w.WriteLine($"{string.Join("、", roots)} · 不小于 {ByteSize.Format(minSize)} 的压缩包", ConsoleColor.DarkGray);

        var finder = new ExtractedArchiveFinder();
        var searchOptions = ArchiveSearchOptions.ForCurrentUser(minSize);
        ArchiveSearchResult result;
        try
        {
            result = CliApp.RunWithProgress(
                () => finder.Find(roots, searchOptions, context.CancellationToken),
                _ => $"{finder.Stage} · 已检查 {finder.ArchivesChecked:N0} 个压缩包",
                context);
        }
        catch (OperationCanceledException)
        {
            context.Stderr.WriteLine("已取消。");
            return CliApp.ExitCancelled;
        }

        Render(result, context);
        if (!options.RemoveDuplicates)
        {
            if (result.Archives.Any(a => a.SuggestRemoval))
            {
                w.WriteLine("  把 [可移除] 的压缩包移入隔离区（可以恢复）：", ConsoleColor.DarkGray);
                w.WriteLine("    " + CommandLine(options, "--remove"), ConsoleColor.White);
                w.WriteLine();
            }

            return CliApp.ExitOk;
        }

        return Remove(result, searchOptions.Guard!, context);
    }

    private static void Render(ArchiveSearchResult result, CliContext context)
    {
        var w = context.Style;
        w.WriteLine();
        if (result.Archives.Count == 0)
        {
            w.WriteLine($"  没有找到已经解压过的压缩包（检查了 {result.ArchivesChecked:N0} 个，用时 {result.ElapsedSeconds:0.0} 秒）。");
            w.WriteLine();
            return;
        }

        w.Write($"■ 找到 {result.Archives.Count:N0} 个看起来已经解压过的压缩包，其中可以放心移除的有 {ByteSize.Format(result.SuggestedBytes)}", ConsoleColor.Cyan);
        w.WriteLine($"  检查了 {result.ArchivesChecked:N0} 个，用时 {result.ElapsedSeconds:0.0} 秒", ConsoleColor.DarkGray);

        var shown = result.Archives.Take(context.Options.Top).ToList();
        foreach (var archive in shown)
        {
            var color = archive.Evidence switch
            {
                ExtractionEvidence.Verified => ConsoleColor.Green,
                ExtractionEvidence.SameNameFolder => ConsoleColor.DarkYellow,
                _ => ConsoleColor.DarkGray,
            };
            w.WriteLine();
            w.Write($"  [{DisplayText.ArchiveTag(archive.Evidence)}] ", color);
            w.Write(TextWidth.PadLeft(ByteSize.Format(archive.Bytes), 10) + "  ");
            w.WriteLine(TextWidth.TruncateMiddle(archive.Path, Math.Max(20, context.Width - 26)));
            if (archive.Parts.Count > 1)
            {
                w.WriteLine($"             共 {archive.Parts.Count} 个分卷", ConsoleColor.DarkGray);
            }

            w.WriteLine($"             {DisplayText.ArchiveEvidence(archive)}", ConsoleColor.DarkGray);
            w.WriteLine($"             → {TextWidth.TruncateMiddle(archive.ExtractedTo, Math.Max(20, context.Width - 16))}", ConsoleColor.DarkGray);
        }

        if (result.Archives.Count > shown.Count)
        {
            w.WriteLine();
            w.WriteLine($"  还有 {result.Archives.Count - shown.Count:N0} 个没有列出（用 --top 显示更多）。", ConsoleColor.DarkGray);
        }

        w.WriteLine();
        w.WriteLine("  [可移除] zip 里的每个文件都已经解压出来了（大小和修改时间一致），压缩包本身可以删；", ConsoleColor.DarkGray);
        w.WriteLine("  [请确认] rar、7z 读不出文件列表，只是旁边有同名、不比它小的文件夹，请自己确认后手动删除（可以在图形界面里勾选）；", ConsoleColor.DarkGray);
        w.WriteLine("  [不完整] 只找到一部分文件，可能没解压完或解压后改过，建议保留。", ConsoleColor.DarkGray);
        w.WriteLine();
    }

    private static int Remove(ArchiveSearchResult result, SafetyGuard guard, CliContext context)
    {
        var options = context.Options;
        var w = context.Style;
        var selected = result.Archives.Where(a => a.SuggestRemoval).ToList();
        if (selected.Count == 0)
        {
            w.WriteLine("  没有可以放心移除的压缩包（只有逐个文件核对过的 zip 才会自动移除）。");
            return CliApp.ExitOk;
        }

        var preview = new ExtractedArchiveRemover(QuarantineStore.ForDrive, guard).Remove(selected, dryRun: true);
        w.WriteLine($"■ 将把 {selected.Count:N0} 个压缩包（{ByteSize.Format(preview.Bytes)}）移入隔离区。", ConsoleColor.Cyan);
        w.WriteLine(options.PurgeNow
            ? "  移入后立即彻底删除（--purge-now），马上释放空间，不能撤销。解压出来的文件不受影响。"
            : "  隔离区里的文件可以随时恢复；确认没问题后彻底删除才会释放空间。解压出来的文件不受影响。", ConsoleColor.DarkGray);
        foreach (var skipped in preview.Skipped.Take(10))
        {
            w.WriteLine($"  跳过 {TextWidth.TruncateMiddle(skipped.Path, Math.Max(20, context.Width - 40))}：{skipped.Reason}", ConsoleColor.DarkYellow);
        }

        DupesCommand.WarnLowSpace(context, selected
            .GroupBy(a => Path.GetPathRoot(a.Path)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Sum(a => a.Bytes))));

        if (!options.Execute)
        {
            w.WriteLine();
            w.WriteLine("  这是模拟运行，没有移动任何文件。确认无误后运行：", ConsoleColor.Cyan);
            w.WriteLine("    " + CommandLine(options, "--remove --execute"), ConsoleColor.White);
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

            w.Write(options.PurgeNow ? "  确定移入隔离区并立即彻底删除吗（不能撤销）？输入 y 确认，其他任意键取消：" : "  确定移入隔离区吗？输入 y 确认，其他任意键取消：", ConsoleColor.Yellow);
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

        var purged = true;
        using (log)
        {
            var removal = new ExtractedArchiveRemover(QuarantineStore.ForDrive, guard, log).Remove(selected, dryRun: false, context.CancellationToken);
            w.WriteLine();
            w.WriteLine($"  已把 {removal.Files:N0} 个文件（{ByteSize.Format(removal.Bytes)}）移入隔离区。", ConsoleColor.Green);
            foreach (var skipped in removal.Skipped.Take(10))
            {
                w.WriteLine($"  跳过 {TextWidth.TruncateMiddle(skipped.Path, Math.Max(20, context.Width - 40))}：{skipped.Reason}", ConsoleColor.DarkYellow);
            }

            var batches = removal.Batches.Select(b => (QuarantineStore.ForDrive(b.DriveRoot), b.SessionId)).ToList();
            if (options.PurgeNow)
            {
                purged = QuarantineCommand.PurgeNow(context, batches, log);
            }
            else
            {
                foreach (var (_, id) in batches)
                {
                    w.WriteLine($"  撤销：diskslim quarantine restore {id}", ConsoleColor.Cyan);
                    w.WriteLine($"  确认没问题后释放空间：diskslim quarantine purge {id} --execute", ConsoleColor.DarkGray);
                }
            }
        }

        w.WriteLine($"  操作日志：{log.FilePath}", ConsoleColor.DarkGray);
        return purged ? CliApp.ExitOk : CliApp.ExitError;
    }

    private static string CommandLine(CliOptions options, string extra)
    {
        var parts = new List<string> { "diskslim archives" };
        parts.AddRange(options.DupesRoots.Select(r => r.Contains(' ') ? $"\"{r}\"" : r));
        if (options.MinSize is { } minSize && minSize != ArchiveSearchOptions.DefaultMinSize)
        {
            parts.Add($"--min-size {minSize}");
        }

        if (options.PurgeNow)
        {
            parts.Add("--purge-now");
        }

        parts.Add(extra);
        return string.Join(" ", parts);
    }
}
