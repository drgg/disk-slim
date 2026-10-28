using System.Text.Json;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class CleanupExecutorTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly string _drive;

    public CleanupExecutorTests() => _drive = Path.GetPathRoot(_dir.Root)!;

    public void Dispose() => _dir.Dispose();

    private CleanupPlan PlanFor(params CleanupRule[] rules) =>
        new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>())
            .Plan(new RuleSet { Rules = rules }, _drive, rules.Select(r => r.Id).ToList());

    private static CleanupRule DeleteRule(string path, int? minAgeDays = null) => new()
    {
        Id = "cache",
        Name = "缓存",
        Risk = RiskLevel.Low,
        Action = CleanupAction.Delete,
        Paths = [path],
        MinAgeDays = minAgeDays,
    };

    [Fact]
    public void Dry_run_changes_nothing_and_predicts_the_real_run()
    {
        _dir.WriteFile("cache/a.bin", 100, Old);
        _dir.WriteFile("cache/sub/b.bin", 200, Old);
        _dir.WriteFile("cache/sub/deeper/c.bin", 300, Old);
        _dir.WriteFile("cache/fresh.bin", 50);
        _dir.SetDirectoryTime("cache/sub/deeper", Old);
        _dir.SetDirectoryTime("cache/sub", Old);
        var plan = PlanFor(DeleteRule(_dir.PathOf("cache"), minAgeDays: 7));

        var preview = new CleanupExecutor().Execute(plan, dryRun: true);

        Assert.True(File.Exists(_dir.PathOf("cache/sub/deeper/c.bin")));
        var predicted = Assert.Single(preview.Rules);
        Assert.Equal((3L, 600L, 2L, 1L), (predicted.Files, predicted.Bytes, predicted.Directories, predicted.SkippedTooNew));

        var result = new CleanupExecutor().Execute(plan, dryRun: false);

        var actual = Assert.Single(result.Rules);
        Assert.Equal((predicted.Files, predicted.Bytes, predicted.Directories, predicted.SkippedTooNew), (actual.Files, actual.Bytes, actual.Directories, actual.SkippedTooNew));
        Assert.Equal(0, actual.Failed);
    }

    [Fact]
    public void Scan_report_and_clean_plan_agree_on_reclaimable_bytes()
    {
        _dir.WriteFile("data/cache/old.bin", 1000, Old);
        _dir.WriteFile("data/cache/deep/old.bin", 2000, Old);
        _dir.WriteFile("data/cache/new.bin", 400);
        _dir.WriteFile("data/cache/readonly.bin", 300, Old, FileAttributes.ReadOnly);
        var extracted = _dir.WriteFile("data/cache/extracted.dll", 200);
        File.SetLastWriteTimeUtc(extracted, Old);
        _dir.WriteFile("data/cache/profile/settings.json", 50, Old);
        var rules = new CleanupRule[]
        {
            new() { Id = "profile", Name = "配置", Risk = RiskLevel.High, Paths = [_dir.PathOf("data/cache/profile")] },
            DeleteRule(_dir.PathOf("data/cache"), minAgeDays: 1),
        };

        var evaluator = new RuleEvaluator(_drive);
        // 必须按顺序评估每一条规则（Select(...).Last() 会被 LINQ 优化成只算最后一条）。
        var reported = rules.Select(r => evaluator.Evaluate(r)).ToList()[^1];
        var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>())
            .Plan(new RuleSet { Rules = rules }, _drive, ["cache"]);
        var preview = new CleanupExecutor().Execute(plan, dryRun: true).Rules.Single();

        Assert.Equal(3000, reported.ReclaimableBytes);
        Assert.Equal(reported.ReclaimableBytes, preview.Bytes);
    }

    [Fact]
    public void Deletes_old_files_keeps_new_ones_and_the_root_directory()
    {
        _dir.WriteFile("cache/old.tmp", 100, Old);
        _dir.WriteFile("cache/new.tmp", 100);
        _dir.WriteFile("cache/emptied/old.tmp", 100, Old);
        _dir.WriteFile("cache/mixed/old.tmp", 100, Old);
        _dir.WriteFile("cache/mixed/new.tmp", 100);
        _dir.SetDirectoryTime("cache/emptied", Old);
        _dir.SetDirectoryTime("cache/mixed", Old);

        new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("cache"), minAgeDays: 1)), dryRun: false);

        Assert.True(Directory.Exists(_dir.PathOf("cache")));
        Assert.False(File.Exists(_dir.PathOf("cache/old.tmp")));
        Assert.True(File.Exists(_dir.PathOf("cache/new.tmp")));
        Assert.False(Directory.Exists(_dir.PathOf("cache/emptied")));
        Assert.False(File.Exists(_dir.PathOf("cache/mixed/old.tmp")));
        Assert.True(File.Exists(_dir.PathOf("cache/mixed/new.tmp")));
    }

    [Fact]
    public void Keeps_recently_created_empty_directories()
    {
        _dir.WriteFile("temp/old.tmp", 1, Old);
        _dir.CreateDirectory("temp/just-created"); // 可能是正在运行的安装程序刚建的

        new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("temp"), minAgeDays: 1)), dryRun: false);

        Assert.True(Directory.Exists(_dir.PathOf("temp/just-created")));
    }

    [Fact]
    public void Skips_read_only_and_system_files()
    {
        _dir.WriteFile("cache/readonly.bin", 10, Old, FileAttributes.ReadOnly);
        _dir.WriteFile("cache/system.bin", 10, Old, FileAttributes.System);
        _dir.WriteFile("cache/hidden.bin", 10, Old, FileAttributes.Hidden);

        var outcome = new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: false).Rules[0];

        Assert.True(File.Exists(_dir.PathOf("cache/readonly.bin")));
        Assert.True(File.Exists(_dir.PathOf("cache/system.bin")));
        Assert.False(File.Exists(_dir.PathOf("cache/hidden.bin")));
        Assert.Equal(2, outcome.SkippedProtected);
    }

    [Fact]
    public void Never_follows_or_deletes_junctions()
    {
        using var precious = new TestDirectory();
        precious.WriteFile("thesis.docx", 1000, Old);
        _dir.WriteFile("cache/old.tmp", 10, Old);
        _dir.CreateJunction("cache/link-to-documents", precious.Root);

        var outcome = new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: false).Rules[0];

        Assert.True(File.Exists(precious.PathOf("thesis.docx")));
        Assert.True(Directory.Exists(_dir.PathOf("cache/link-to-documents")));
        Assert.Equal(1, outcome.SkippedLinks);
        Assert.Equal(1, outcome.Files);
    }

    [Fact]
    public void Leaves_paths_owned_by_other_rules_untouched()
    {
        _dir.WriteFile("appdata/chat/history.db", 100, Old);
        _dir.WriteFile("appdata/keep-me.cfg", 5, Old);
        _dir.WriteFile("appdata/junk.tmp", 10, Old);
        var chat = new CleanupRule { Id = "chat", Name = "聊天记录", Risk = RiskLevel.High, Paths = [_dir.PathOf("appdata/chat")] };
        var config = new CleanupRule { Id = "config", Name = "配置", Risk = RiskLevel.High, Paths = [_dir.PathOf("appdata/keep-me.cfg")] };
        var junk = DeleteRule(_dir.PathOf("appdata"));
        var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>())
            .Plan(new RuleSet { Rules = [chat, config, junk] }, _drive, ["cache"]);

        new CleanupExecutor().Execute(plan, dryRun: false);

        Assert.True(File.Exists(_dir.PathOf("appdata/chat/history.db")));
        Assert.True(File.Exists(_dir.PathOf("appdata/keep-me.cfg")));
        Assert.False(File.Exists(_dir.PathOf("appdata/junk.tmp")));
    }

    [Fact]
    public void Files_in_use_are_reported_and_the_rest_is_still_cleaned()
    {
        var locked = _dir.WriteFile("cache/locked.bin", 64, Old);
        _dir.WriteFile("cache/free.bin", 32, Old);

        RuleOutcome outcome;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: false).Rules[0];
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(_dir.PathOf("cache/free.bin")));
        Assert.Equal(1, outcome.Failed);
        Assert.Equal(64, outcome.FailedBytes);
        Assert.Equal("正在被其他程序使用", Assert.Single(outcome.Failures).Reason);
        Assert.Equal((1L, 32L), (outcome.Files, outcome.Bytes));
    }

    [Fact]
    public void Recycle_action_moves_files_to_recycle_bin_and_keeps_directories()
    {
        _dir.WriteFile("downloads/setup.exe", 500, Old);
        _dir.WriteFile("downloads/sub/other.exe", 300, Old);
        var ops = new RecordingFileOperations();
        var rule = new CleanupRule { Id = "installers", Name = "安装包", Risk = RiskLevel.Medium, Action = CleanupAction.Recycle, Paths = [_dir.PathOf("downloads/*.exe")] };

        var result = new CleanupExecutor(ops).Execute(PlanFor(rule), dryRun: false);

        Assert.Equal([_dir.PathOf("downloads/setup.exe")], ops.Recycled, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(ops.Deleted);
        Assert.True(File.Exists(_dir.PathOf("downloads/sub/other.exe")));
        Assert.Equal(500, result.RecycledBytes);
        Assert.Equal(0, result.DeletedBytes);
    }

    [Fact]
    public void Empties_the_recycle_bin_only_when_executing()
    {
        var ops = new RecordingFileOperations { Bin = new RecycleBinInfo(4096, 3) };
        var rule = new CleanupRule { Id = "bin", Name = "回收站", Risk = RiskLevel.Medium, Action = CleanupAction.EmptyRecycleBin, Paths = [PathExpander.RecycleBinToken] };
        var plan = new CleanupPlan
        {
            DriveRoot = _drive,
            Rules = [new PlannedRule { Rule = rule, Status = PlanStatus.Ready }],
            Available = [],
        };

        var preview = new CleanupExecutor(ops).Execute(plan, dryRun: true);
        Assert.Equal(0, ops.EmptyCalls);
        Assert.Equal((3L, 4096L), (preview.Rules[0].Files, preview.Rules[0].Bytes));

        new CleanupExecutor(ops).Execute(plan, dryRun: false);
        Assert.Equal(1, ops.EmptyCalls);
    }

    [Fact]
    public void Rules_that_are_not_ready_are_not_touched()
    {
        _dir.WriteFile("cache/a.tmp", 10, Old);
        var rule = DeleteRule(_dir.PathOf("cache")) with { Processes = ["someapp"] };
        var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "someapp" })
            .Plan(new RuleSet { Rules = [rule] }, _drive, ["cache"]);

        new CleanupExecutor().Execute(plan, dryRun: false);

        Assert.True(File.Exists(_dir.PathOf("cache/a.tmp")));
    }

    [Fact]
    public void Cancellation_stops_before_deleting()
    {
        _dir.WriteFile("cache/a.tmp", 10, Old);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new CleanupExecutor().Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: false, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.Rules[0].Cancelled);
        Assert.True(File.Exists(_dir.PathOf("cache/a.tmp")));
    }

    [Fact]
    public void Writes_every_operation_to_the_log()
    {
        _dir.WriteFile("cache/a.tmp", 10, Old);
        _dir.WriteFile("cache/sub/b.tmp", 20, Old);
        var logPath = _dir.PathOf("logs/clean.jsonl");

        using (var log = new JsonLinesOperationLog(logPath))
        {
            var result = new CleanupExecutor(log: log).Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: false);
            Assert.Equal(log.FilePath, result.LogPath);
        }

        var entries = File.ReadAllLines(logPath).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        var events = entries.Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Equal("run-start", events[0]);
        Assert.Equal("run-end", events[^1]);
        Assert.Equal(2, events.Count(e => e == "deleted"));
        Assert.Contains("removed-dir", events);
        var deleted = entries.First(e => e.GetProperty("event").GetString() == "deleted");
        Assert.Equal("cache", deleted.GetProperty("rule").GetString());
        Assert.True(deleted.GetProperty("bytes").GetInt64() > 0);
        Assert.False(deleted.TryGetProperty("detail", out _)); // null 字段不写出
    }

    [Fact]
    public void Dry_run_writes_no_log_entries()
    {
        _dir.WriteFile("cache/a.tmp", 10, Old);
        var log = new RecordingLog();

        new CleanupExecutor(log: log).Execute(PlanFor(DeleteRule(_dir.PathOf("cache"))), dryRun: true);

        Assert.Empty(log.Entries);
    }

    private sealed class RecordingFileOperations : IFileOperations
    {
        public List<string> Deleted { get; } = [];

        public List<string> Recycled { get; } = [];

        public int EmptyCalls { get; private set; }

        public RecycleBinInfo? Bin { get; init; }

        public void DeleteFile(string path, PathAnchor anchor)
        {
            Deleted.Add(path);
            new WindowsFileOperations().DeleteFile(path, anchor);
        }

        public bool TryDeleteEmptyDirectory(string path, PathAnchor anchor) => new WindowsFileOperations().TryDeleteEmptyDirectory(path, anchor);

        public void MoveToRecycleBin(string path, PathAnchor anchor)
        {
            // 不碰真实的回收站：核对位置、记录下来，然后删除文件模拟“已移走”。
            HandleOperations.Verify(path, anchor);
            Recycled.Add(path);
            File.Delete(path);
        }

        public RecycleBinInfo? QueryRecycleBin(string driveRoot) => Bin;

        public void EmptyRecycleBin(string driveRoot) => EmptyCalls++;
    }

    private sealed class RecordingLog : IOperationLog
    {
        public List<LogEntry> Entries { get; } = [];

        public string? FilePath => null;

        public void Write(LogEntry entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }
}
