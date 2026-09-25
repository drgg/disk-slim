using System.Text.Json;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class QuarantineTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly QuarantineStore _store;
    private readonly string _drive;
    private readonly SafetyGuard _guard = SafetyGuard.ForCurrentUser();

    public QuarantineTests()
    {
        _store = new QuarantineStore(_dir.PathOf("quarantine"));
        _drive = Path.GetPathRoot(_dir.Root)!;
    }

    public void Dispose() => _dir.Dispose();

    private QuarantineSession MoveIn(params string[] relativePaths)
    {
        var writer = _store.BeginSession("测试", DateTimeOffset.Now);
        foreach (var relative in relativePaths)
        {
            var path = _dir.PathOf(relative);
            writer.Move(path, new FileInfo(path).Length, "test");
        }

        return _store.Find(writer.Id)!;
    }

    [Fact]
    public void Moved_files_are_listed_with_their_original_location()
    {
        _dir.WriteFile("data/model.bin", 300);
        _dir.WriteFile("data/sub/notes.txt", 20);

        var session = MoveIn("data/model.bin", "data/sub/notes.txt");

        Assert.False(File.Exists(_dir.PathOf("data/model.bin")));
        Assert.Equal(2, session.Items.Count);
        Assert.Equal(320, session.TotalBytes);
        Assert.Equal(_dir.PathOf("data/model.bin"), session.Items[0].OriginalPath);
        Assert.EndsWith(".bin", session.Items[0].StoredPath);
        Assert.True(File.Exists(session.Items[0].StoredPath));
        Assert.Equal("测试", Assert.Single(_store.ListSessions()).Description);
    }

    [Fact]
    public void Nothing_is_created_until_the_first_file_is_moved()
    {
        _store.BeginSession("空的", DateTimeOffset.Now);

        Assert.False(Directory.Exists(_store.Root));
        Assert.Empty(_store.ListSessions());
    }

    [Fact]
    public void Restore_puts_files_back_and_removes_the_empty_session()
    {
        _dir.WriteFile("data/sub/notes.txt", 20);
        var session = MoveIn("data/sub/notes.txt");
        Directory.Delete(_dir.PathOf("data/sub")); // 原来的目录也不在了

        var result = _store.Restore(session, _guard);

        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Problems);
        Assert.Equal(20, new FileInfo(_dir.PathOf("data/sub/notes.txt")).Length);
        Assert.Empty(_store.ListSessions());
        Assert.False(Directory.Exists(session.Directory));
    }

    [Fact]
    public void Restore_never_overwrites_an_existing_file()
    {
        _dir.WriteFile("data/a.txt", 20);
        var session = MoveIn("data/a.txt");
        _dir.WriteFile("data/a.txt", 99); // 用户在原位置又放了一个新文件

        var result = _store.Restore(session, _guard);

        Assert.Equal(0, result.Restored);
        Assert.Contains("同名文件", Assert.Single(result.Problems).Reason);
        Assert.Equal(99, new FileInfo(_dir.PathOf("data/a.txt")).Length);
        Assert.Single(_store.ListSessions()); // 仍在隔离区里
    }

    [Fact]
    public void Restore_refuses_protected_locations()
    {
        _dir.WriteFile("data/a.txt", 20);
        var session = MoveIn("data/a.txt");
        var strictGuard = new SafetyGuard([], [_dir.PathOf("data")]);

        var result = _store.Restore(session, strictGuard);

        Assert.Equal(0, result.Restored);
        Assert.Contains("受保护", Assert.Single(result.Problems).Reason);
    }

    [Fact]
    public void Partial_restore_keeps_the_rest_in_quarantine()
    {
        _dir.WriteFile("a.txt", 1);
        _dir.WriteFile("b.txt", 2);
        var session = MoveIn("a.txt", "b.txt");

        _store.Restore(session, _guard, [session.Items[0].Index]);

        Assert.True(File.Exists(_dir.PathOf("a.txt")));
        Assert.Equal(_dir.PathOf("b.txt"), Assert.Single(Assert.Single(_store.ListSessions()).Items).OriginalPath);
    }

    [Fact]
    public void A_failed_move_leaves_the_file_in_place_and_out_of_the_listing()
    {
        var locked = _dir.WriteFile("locked.bin", 10);
        _dir.WriteFile("free.bin", 5);
        var writer = _store.BeginSession("测试", DateTimeOffset.Now);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => writer.Move(locked, 10, "test"));
        }

        writer.Move(_dir.PathOf("free.bin"), 5, "test");

        Assert.True(File.Exists(locked));
        var item = Assert.Single(_store.Find(writer.Id)!.Items);
        Assert.Equal(_dir.PathOf("free.bin"), item.OriginalPath);
    }

    [Fact]
    public void Purge_deletes_the_session_and_logs_it()
    {
        _dir.WriteFile("a.bin", 100, attributes: FileAttributes.ReadOnly);
        var session = MoveIn("a.bin");
        var log = new MemoryLog();

        var result = _store.Purge([session], log);

        Assert.Equal((1, 100L), (result.Purged, result.PurgedBytes));
        Assert.False(Directory.Exists(session.Directory));
        Assert.Contains(log.Entries, e => e.Event == "purged-quarantine" && e.Bytes == 100);
    }

    [Fact]
    public void Operations_outside_the_store_are_refused()
    {
        var fake = new QuarantineSession("x", _dir.PathOf("elsewhere"), DateTimeOffset.Now, "", []);

        Assert.Throws<InvalidOperationException>(() => _store.Purge(fake));
    }

    [Fact]
    public void Manifest_is_json_lines_with_the_original_path()
    {
        _dir.WriteFile("a.bin", 1);
        var session = MoveIn("a.bin");

        var lines = File.ReadAllLines(Path.Join(session.Directory, "manifest.jsonl"));

        var record = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("added", record.GetProperty("event").GetString());
        Assert.Equal(_dir.PathOf("a.bin"), record.GetProperty("originalPath").GetString());
    }

    [Fact]
    public void Quarantine_action_moves_files_and_the_whole_run_can_be_undone()
    {
        _dir.WriteFile("cache/a.bin", 100, Old);
        _dir.WriteFile("cache/deep/b.bin", 200, Old);
        _dir.SetDirectoryTime("cache/deep", Old);
        var rule = new CleanupRule { Id = "big", Name = "大缓存", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("cache")] };
        var plan = new CleanupPlanner(_guard, new HashSet<string>(), _store).Plan(new RuleSet { Rules = [rule] }, _drive, ["big"]);

        var preview = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: true);
        Assert.Equal(300, preview.QuarantinedBytes);
        Assert.Empty(_store.ListSessions());

        var result = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: false);

        Assert.Equal((300L, 0L), (result.QuarantinedBytes, result.DeletedBytes));
        Assert.False(File.Exists(_dir.PathOf("cache/deep/b.bin")));
        Assert.False(Directory.Exists(_dir.PathOf("cache/deep")));
        var session = _store.Find(result.QuarantineSessionId!)!;
        Assert.Contains("大缓存", session.Description);

        _store.Restore(session, _guard);

        Assert.Equal(100, new FileInfo(_dir.PathOf("cache/a.bin")).Length);
        Assert.Equal(200, new FileInfo(_dir.PathOf("cache/deep/b.bin")).Length);
    }

    [Fact]
    public void Purge_rule_only_removes_expired_sessions()
    {
        _dir.WriteFile("old.bin", 100);
        _dir.WriteFile("new.bin", 50);
        var oldWriter = _store.BeginSession("旧的", DateTimeOffset.Now.AddDays(-10));
        oldWriter.Move(_dir.PathOf("old.bin"), 100, "test");
        var newWriter = _store.BeginSession("新的", DateTimeOffset.Now);
        newWriter.Move(_dir.PathOf("new.bin"), 50, "test");
        var rule = new CleanupRule { Id = "expired", Name = "到期", Risk = RiskLevel.Low, Action = CleanupAction.PurgeQuarantine, Paths = [PathExpander.QuarantineToken], MinAgeDays = 7 };

        var evaluated = new RuleEvaluator(_drive, quarantine: _store).Evaluate(rule);
        Assert.Equal((150L, 100L), (evaluated.TotalBytes, evaluated.ReclaimableBytes));

        var plan = new CleanupPlanner(_guard, new HashSet<string>(), _store).Plan(new RuleSet { Rules = [rule] }, _drive, []);
        Assert.Equal(PlanStatus.Ready, plan.Rules.Single().Status);
        var result = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: false);

        Assert.Equal(100, result.DeletedBytes);
        Assert.Equal("新的", Assert.Single(_store.ListSessions()).Description);
    }

    [Fact]
    public void The_real_quarantine_and_logs_are_protected_from_cleaning_rules()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.NotNull(_guard.Check(QuarantineStore.DefaultRoot(Path.GetPathRoot(local)!)));
        Assert.NotNull(_guard.Check(Path.Combine(local, "DiskSlim", "logs")));
    }

    private sealed class MemoryLog : IOperationLog
    {
        public List<LogEntry> Entries { get; } = [];

        public string? FilePath => null;

        public void Write(LogEntry entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }
}
