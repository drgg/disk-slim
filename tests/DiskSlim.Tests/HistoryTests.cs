using System.IO.Compression;
using DiskSlim.Cli;
using DiskSlim.Core.History;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Scanning;
using DiskSlim.Desktop.Controls;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Tests;

public sealed class HistoryTests : IDisposable
{
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;
    // 本地时间：快照文件名和保留策略都按本地日期算。
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Local));
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>用 “路径=大小” 列表拼出一份快照；上级目录必须写在下级前面。</summary>
    private static SpaceSnapshot Snap(DateTimeOffset time, long used, params (string Path, long Bytes)[] dirs)
    {
        var list = new List<SnapshotDirectory>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in dirs)
        {
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            var parentIndex = parent is null ? -1 : index[parent.Length == 2 ? parent + "\\" : parent];
            index[path] = list.Count;
            list.Add(new SnapshotDirectory(parentIndex, parentIndex < 0 ? path : Path.GetFileName(path), bytes, 1));
        }

        return new SpaceSnapshot
        {
            Time = time,
            Drive = @"C:\",
            TotalBytes = 500 * GB,
            FreeBytes = 500 * GB - used,
            MinDirectoryBytes = 4 * MB,
            Directories = list,
        };
    }

    // ───── 比较 ─────

    [Fact]
    public void Growth_is_reported_at_the_folder_where_it_happened_not_at_every_ancestor()
    {
        var old = Snap(Now.AddDays(-7), 100 * GB,
            (@"C:\", 100 * GB), (@"C:\Users", 60 * GB), (@"C:\Users\me", 60 * GB), (@"C:\Users\me\AppData", 30 * GB), (@"C:\Users\me\AppData\Foo", 10 * GB), (@"C:\Users\me\AppData\Foo\Cache", 1 * GB));
        var now = Snap(Now, 102 * GB,
            (@"C:\", 102 * GB), (@"C:\Users", 62 * GB), (@"C:\Users\me", 62 * GB), (@"C:\Users\me\AppData", 32 * GB), (@"C:\Users\me\AppData\Foo", 12 * GB), (@"C:\Users\me\AppData\Foo\Cache", 3 * GB));

        var report = GrowthAnalyzer.Compare(old, now);

        var item = Assert.Single(report.Grown);
        Assert.Equal(@"C:\Users\me\AppData\Foo\Cache", item.Path);
        Assert.Equal(2 * GB, item.Delta);
        Assert.False(item.IsRemainder);
        Assert.Equal(1 * GB, item.OldBytes);
        Assert.Empty(report.Shrunk);
        Assert.Equal(2 * GB, report.UsedDelta);
    }

    [Fact]
    public void Growth_spread_over_many_small_folders_is_reported_at_their_parent_as_a_remainder()
    {
        // Foo 涨了 1.5 GB：其中 Cache 单独涨了 1 GB，另外 500 MB 分散在很多小文件夹里。
        var old = Snap(Now.AddDays(-7), 10 * GB, (@"C:\", 10 * GB), (@"C:\Foo", 2 * GB), (@"C:\Foo\Cache", 1 * GB));
        var now = Snap(Now, 11 * GB + 512 * MB, (@"C:\", 11 * GB + 512 * MB), (@"C:\Foo", 3 * GB + 512 * MB), (@"C:\Foo\Cache", 2 * GB));

        var report = GrowthAnalyzer.Compare(old, now);

        Assert.Equal(2, report.Grown.Count);
        Assert.Equal((@"C:\Foo\Cache", 1 * GB, false), (report.Grown[0].Path, report.Grown[0].Delta, report.Grown[0].IsRemainder));
        Assert.Equal((@"C:\Foo", 512 * MB, true), (report.Grown[1].Path, report.Grown[1].Delta, report.Grown[1].IsRemainder));
        Assert.Equal(1536 * MB, report.Grown[1].FolderDelta);
        Assert.Equal(1536 * MB, report.Grown.Sum(g => g.Delta)); // 互不重叠：相加正好是总增长
    }

    [Fact]
    public void Growth_and_shrinkage_in_sibling_folders_do_not_cancel_out()
    {
        var old = Snap(Now.AddDays(-1), 20 * GB, (@"C:\", 20 * GB), (@"C:\Data", 10 * GB), (@"C:\Data\Videos", 1 * GB), (@"C:\Data\Temp", 5 * GB));
        var now = Snap(Now, 20 * GB, (@"C:\", 20 * GB), (@"C:\Data", 10 * GB), (@"C:\Data\Videos", 5 * GB), (@"C:\Data\Temp", 1 * GB));

        var report = GrowthAnalyzer.Compare(old, now);

        Assert.Equal(@"C:\Data\Videos", Assert.Single(report.Grown).Path);
        var shrunk = Assert.Single(report.Shrunk);
        Assert.Equal(@"C:\Data\Temp", shrunk.Path);
        Assert.Equal(-4 * GB, shrunk.Delta);
    }

    [Fact]
    public void Folders_that_were_too_small_to_record_show_as_below_the_threshold()
    {
        var old = Snap(Now.AddDays(-3), 10 * GB, (@"C:\", 10 * GB), (@"C:\Users", 5 * GB));
        var now = Snap(Now, 12 * GB, (@"C:\", 12 * GB), (@"C:\Users", 5 * GB), (@"C:\Games", 2 * GB));

        var item = Assert.Single(GrowthAnalyzer.Compare(old, now).Grown);

        Assert.Equal(@"C:\Games", item.Path);
        Assert.Null(item.OldBytes);
        Assert.Equal("< 4.00 MB → 2.00 GB", Core.Presentation.DisplayText.GrowthDetail(item, 4 * MB));
    }

    [Fact]
    public void Changes_below_the_minimum_are_not_listed()
    {
        var old = Snap(Now.AddDays(-7), 10 * GB, (@"C:\", 10 * GB), (@"C:\A", 1 * GB));
        var now = Snap(Now, 10 * GB + 50 * MB, (@"C:\", 10 * GB + 50 * MB), (@"C:\A", 1 * GB + 50 * MB));

        Assert.Empty(GrowthAnalyzer.Compare(old, now).Grown);
        Assert.Equal(@"C:\A", Assert.Single(GrowthAnalyzer.Compare(old, now, minBytes: 10 * MB).Grown).Path);
    }

    [Fact]
    public void Rule_changes_compare_by_id_and_need_both_snapshots_to_have_checked_rules()
    {
        var old = Snap(Now.AddDays(-7), 10 * GB, (@"C:\", 10 * GB)) with
        {
            Rules = [new SnapshotRule("wechat", "微信", 5 * GB), new SnapshotRule("temp", "临时文件", 2 * GB), new SnapshotRule("tiny", "小", 1 * MB)],
        };
        var now = Snap(Now, 10 * GB, (@"C:\", 10 * GB)) with
        {
            Rules = [new SnapshotRule("wechat", "微信", 6 * GB), new SnapshotRule("tiny", "小", 2 * MB)],
        };

        var rules = GrowthAnalyzer.Compare(old, now).Rules!;

        Assert.Equal(["temp", "wechat"], rules.Select(r => r.Id)); // 按变化量排序，1 MB 的变化不列出
        Assert.Equal(-2 * GB, rules[0].Delta); // 这次没有命中 = 0
        Assert.Null(GrowthAnalyzer.Compare(old with { Rules = null }, now).Rules);
    }

    [Fact]
    public void Snapshots_of_different_drives_cannot_be_compared()
    {
        var c = Snap(Now.AddDays(-1), GB, (@"C:\", GB));
        var d = c with { Drive = @"D:\", Time = Now };

        Assert.Throws<ArgumentException>(() => GrowthAnalyzer.Compare(c, d));
    }

    [Fact]
    public void Baseline_is_the_newest_snapshot_at_least_that_old_or_else_the_oldest()
    {
        SnapshotSummary At(double daysAgo, bool details = true) => new() { Time = Now.AddDays(-daysAgo), File = details ? "x" : null };
        var history = new[] { At(40, details: false), At(30), At(8), At(6), At(1), At(0) };

        Assert.Equal(Now.AddDays(-8), GrowthAnalyzer.PickBaseline(history, Now, TimeSpan.FromDays(7))!.Time);
        Assert.Equal(Now.AddDays(-30), GrowthAnalyzer.PickBaseline(history, Now, TimeSpan.FromDays(365))!.Time); // 没有明细的不能用
        Assert.Equal(Now.AddDays(-1), GrowthAnalyzer.PickBaseline(history, Now, TimeSpan.Zero)!.Time); // 0 = 上一次
        Assert.Null(GrowthAnalyzer.PickBaseline([At(0)], Now, TimeSpan.FromDays(7)));
    }

    // ───── 生成和保存 ─────

    [Fact]
    public void Snapshot_keeps_only_folders_above_the_threshold_in_parent_first_order()
    {
        _dir.WriteFile("big/a/file.bin", 5000);
        _dir.WriteFile("big/b/file.bin", 3000);
        _dir.WriteFile("small/file.bin", 100);
        var scan = new TreeScanner().Scan(_dir.Root);
        var report = new ScanReport
        {
            ToolVersion = "test",
            GeneratedAt = Now,
            IsElevated = false,
            Drive = new DriveSummary(_dir.Root, "", "NTFS", 1000, 500),
            RulesChecked = false,
        };

        var snapshot = SpaceSnapshot.Create(report, scan, minDirectoryBytes: 2000);

        Assert.Equal([(-1, _dir.Root, 8100L), (0, "big", 8000), (1, "a", 5000), (1, "b", 3000)],
            snapshot.Directories.Select(d => (d.Parent, d.Name, d.Bytes)));
        Assert.Null(snapshot.Rules);
        Assert.Throws<ArgumentException>(() => SpaceSnapshot.Create(report with { Drive = report.Drive with { Root = @"C:\" } }, scan));
    }

    [Fact]
    public void Saved_snapshots_load_back_and_appear_in_the_history()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var first = Snap(Now.AddHours(-2), 10 * GB, (@"C:\", 10 * GB), (@"C:\用户", 4 * GB));
        var second = Snap(Now, 11 * GB, (@"C:\", 11 * GB));

        var saved = store.Save(first);
        store.Save(second);
        File.AppendAllText(_dir.PathOf(@"history\C\index.jsonl"), "{\"time\": 半行\n"); // 断电留下的半行

        var history = store.History(@"C:\");
        Assert.Equal([first.Time, second.Time], history.Select(h => h.Time));
        Assert.Equal(10 * GB, history[0].UsedBytes);
        var loaded = store.Load(@"c:", saved)!;
        Assert.Equal(first.Directories, loaded.Directories);
        Assert.Equal("用户", loaded.Directories[1].Name);
        Assert.False(File.Exists(_dir.PathOf(@"history\C\" + saved.File + ".tmp")));
    }

    [Fact]
    public void Two_scans_in_the_same_second_do_not_overwrite_each_other()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var a = store.Save(Snap(Now, GB, (@"C:\", GB)));
        var b = store.Save(Snap(Now, 2 * GB, (@"C:\", 2 * GB)));

        Assert.NotEqual(a.File, b.File);
        Assert.Equal(2 * GB, store.Load(@"C:\", b)!.ScannedBytes);
    }

    [Fact]
    public void Corrupt_snapshot_files_load_as_null()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var saved = store.Save(Snap(Now, GB, (@"C:\", GB)));
        File.WriteAllText(_dir.PathOf(@"history\C\" + saved.File), "不是 gzip");

        Assert.Null(store.Load(@"C:\", saved));
        Assert.Null(store.Load(@"C:\", saved with { File = @"..\..\secret.json.gz" }));
    }

    [Fact]
    public void Pruning_keeps_recent_snapshots_and_one_per_day_and_never_touches_other_files()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var folder = _dir.CreateDirectory(@"history\C");
        string[] names =
        [
            "20260925-130000.json.gz", "20260925-090000.json.gz",  // 今天：全部保留
            "20260924-100000.json.gz", "20260924-080000.json.gz",  // 昨天：全部保留
            "20260920-230000.json.gz", "20260920-080000.json.gz",  // 5 天前：只留最后一份
            "20260601-120000.json.gz",                              // 超过 90 天：删除
            "notes.json.gz", "20260101-000000.txt",                 // 不是快照：不碰
        ];
        foreach (var name in names)
        {
            File.WriteAllText(Path.Join(folder, name), "x");
        }

        var deleted = store.Prune(@"C:\", Now);

        Assert.Equal(["20260601-120000.json.gz", "20260920-080000.json.gz"], deleted.Order());
        Assert.Equal(names.Length - 2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void History_marks_entries_whose_snapshot_was_pruned()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var saved = store.Save(Snap(Now.AddDays(-200), GB, (@"C:\", GB)));
        store.Save(Snap(Now, GB, (@"C:\", GB))); // 保存时顺便清理：200 天前的明细被删除

        var history = store.History(@"C:\");

        Assert.Equal(2, history.Count);
        Assert.False(history[0].HasDetails);
        Assert.Null(store.Load(@"C:\", saved));
    }

    [Fact]
    public void Compare_with_past_uses_the_saved_history()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        store.Save(Snap(Now.AddDays(-8), 10 * GB, (@"C:\", 10 * GB), (@"C:\Temp", 1 * GB)));
        var current = Snap(Now, 13 * GB, (@"C:\", 13 * GB), (@"C:\Temp", 4 * GB));

        var report = store.CompareWithPast(current, TimeSpan.FromDays(7))!;

        Assert.Equal(3 * GB, report.UsedDelta);
        Assert.Equal(@"C:\Temp", Assert.Single(report.Grown).Path);
        Assert.Null(new SnapshotStore(_dir.PathOf("empty")).CompareWithPast(current, TimeSpan.FromDays(7)));
    }

    // ───── 命令行 ─────

    private (int ExitCode, string Output) RunCli(SnapshotStore store, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run([.. args, "--no-color"], stdout, stderr, new StringReader(""), interactive: false, history: store);
        return (exitCode, stdout.ToString() + stderr);
    }

    [Fact]
    public void Growth_command_explains_what_to_do_without_history()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));

        var (exitCode, output) = RunCli(store, "growth");
        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("还没有 C:\\ 的扫描记录", output);

        store.Save(Snap(Now, GB, (@"C:\", GB)));
        Assert.Contains("只有一次扫描记录", RunCli(store, "growth").Output);
    }

    [Fact]
    public void Growth_command_compares_the_latest_scan_with_the_chosen_age()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var recent = DateTimeOffset.Now;
        store.Save(Snap(recent.AddDays(-30), 10 * GB, (@"C:\", 10 * GB), (@"C:\Videos", 1 * GB)));
        store.Save(Snap(recent.AddDays(-2), 12 * GB, (@"C:\", 12 * GB), (@"C:\Videos", 3 * GB)));
        store.Save(Snap(recent, 15 * GB, (@"C:\", 15 * GB), (@"C:\Videos", 6 * GB)));

        var (exitCode, output) = RunCli(store, "growth");
        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("已用空间 +5.00 GB", output); // 默认 7 天：和不晚于 7 天前的最近一份（30 天前）比较
        Assert.Contains(@"C:\Videos", output);
        Assert.Contains("1.00 GB → 6.00 GB", output);

        var previous = RunCli(store, "growth", "--days", "0").Output;
        Assert.Contains("已用空间 +3.00 GB", previous);
        Assert.Contains("3.00 GB → 6.00 GB", previous);

        var list = RunCli(store, "growth", "list").Output;
        Assert.Contains("共 3 次扫描", list);
        Assert.Contains("+2.00 GB", list);
    }

    [Theory]
    [InlineData("growth", "--no-history")]
    [InlineData("growth", "--execute")]
    [InlineData("growth", "restore")]
    [InlineData("growth", "--min-size", "0")]
    [InlineData("clean", "--no-history")]
    public void Growth_options_are_checked(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Fact]
    public void Growth_options_parse()
    {
        var options = CliParser.Parse(["growth", "list", "--days", "30", "--min-size", "50MB", "-n", "5"]).Options!;

        Assert.Equal(CliCommand.Growth, options.Command);
        Assert.True(options.ListHistory);
        Assert.Equal(30, options.Days);
        Assert.Equal(50 * MB, options.MinSize);
        Assert.Equal(5, options.Top);
        Assert.True(CliParser.Parse(["--no-history"]).Options!.NoHistory);
    }

    // ───── 图形界面 ─────

    [Fact]
    public void Growth_page_explains_the_first_scan()
    {
        var page = new GrowthViewModel(new SnapshotStore(_dir.PathOf("history")));
        var current = Snap(Now, GB, (@"C:\", GB));

        page.Show(current, [SnapshotSummary.Of(current, "x.json.gz")]);

        Assert.False(page.HasComparison);
        Assert.False(page.HasBaselines);
        Assert.Contains("第一次扫描", page.EmptyText);
        Assert.Single(page.Usage);
    }

    [Fact]
    public void Growth_page_compares_with_about_a_week_ago_and_can_switch()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        store.Save(Snap(Now.AddDays(-9), 10 * GB, (@"C:\", 10 * GB), (@"C:\Temp", 1 * GB)));
        store.Save(Snap(Now.AddDays(-1), 11 * GB, (@"C:\", 11 * GB), (@"C:\Temp", 2 * GB)));
        var current = Snap(Now, 14 * GB, (@"C:\", 14 * GB), (@"C:\Temp", 5 * GB));
        var page = new GrowthViewModel(store);

        page.Show(current, store.History(@"C:\")); // 这次没有保存

        Assert.True(page.HasComparison);
        Assert.Equal(Now.AddDays(-9), page.BaselineTime);
        Assert.Equal(3, page.Usage.Count);
        Assert.Equal("+4.00 GB", Assert.Single(page.Grown).DeltaText);
        Assert.Equal(("Temp", @"C:\"), (page.Grown[0].Name, page.Grown[0].Location));

        page.SelectBaselineAt(Now.AddDays(-1));
        Assert.Equal("+3.00 GB", Assert.Single(page.Grown).DeltaText);
        Assert.Contains("已用空间 +3.00 GB", page.Headline);
    }

    [Fact]
    public void Usage_chart_axis_uses_round_ticks_with_enough_decimals()
    {
        var points = new[] { new UsagePoint(Now, 196 * GB + 900 * MB, 0, true), new UsagePoint(Now.AddDays(1), 197 * GB + 100 * MB, 0, true) };

        var (low, high, step) = UsageChart.YScale(points);

        Assert.True(low <= points[0].UsedBytes && high >= points[1].UsedBytes);
        Assert.True((high - low) / step <= 4);
        Assert.Equal("196.8 GB", UsageChart.TickLabel(low, step));
        Assert.Equal("200 GB", UsageChart.TickLabel(200 * GB, 10 * GB));
    }

    [Fact]
    public void Snapshot_files_are_gzip_compressed_json()
    {
        var store = new SnapshotStore(_dir.PathOf("history"));
        var saved = store.Save(Snap(Now, GB, (@"C:\", GB)));

        using var gzip = new GZipStream(File.OpenRead(_dir.PathOf(@"history\C\" + saved.File)), CompressionMode.Decompress);
        var json = new StreamReader(gzip).ReadToEnd();

        Assert.StartsWith("{\"version\":1", json);
        Assert.DoesNotContain("usedBytes", json);
    }
}
