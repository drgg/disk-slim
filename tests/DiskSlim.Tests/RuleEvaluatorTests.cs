using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class RuleEvaluatorTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly string _drive;

    public RuleEvaluatorTests() => _drive = Path.GetPathRoot(_dir.Root)!;

    public void Dispose() => _dir.Dispose();

    private static CleanupRule Rule(string id, params string[] paths) => new()
    {
        Id = id,
        Name = id,
        Paths = paths,
        Risk = RiskLevel.Low,
    };

    [Fact]
    public void Measures_matched_directories()
    {
        _dir.WriteFile("cache/a.bin", 100);
        _dir.WriteFile("cache/sub/b.bin", 50);

        var result = new RuleEvaluator(_drive).Evaluate(Rule("r", _dir.PathOf("cache")));

        var match = Assert.Single(result.Matches);
        Assert.Equal(150, match.TotalBytes);
        Assert.Equal(150, result.ReclaimableBytes);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void MinAgeDays_limits_reclaimable_bytes()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        _dir.WriteFile("temp/old.tmp", 100, now.UtcDateTime.AddDays(-3));
        _dir.WriteFile("temp/new.tmp", 30, now.UtcDateTime.AddHours(-2));
        var rule = Rule("temp", _dir.PathOf("temp")) with { MinAgeDays = 1 };

        var result = new RuleEvaluator(_drive, time: new FixedTime(now)).Evaluate(rule);

        Assert.Equal(130, result.TotalBytes);
        Assert.Equal(100, result.ReclaimableBytes);
    }

    [Fact]
    public void Measures_single_files()
    {
        var now = DateTimeOffset.UtcNow;
        _dir.WriteFile("hiberfil.sys", 4096, now.UtcDateTime.AddDays(-30));

        var result = new RuleEvaluator(_drive).Evaluate(Rule("file", _dir.PathOf("hiberfil.sys")));

        Assert.Equal(4096, result.TotalBytes);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void Same_path_is_only_counted_by_the_first_rule()
    {
        _dir.WriteFile("cache/a.bin", 100);
        var evaluator = new RuleEvaluator(_drive);

        var first = evaluator.Evaluate(Rule("first", _dir.PathOf("cache")));
        var second = evaluator.Evaluate(Rule("second", _dir.PathOf("cache"), _dir.PathOf("cache/a.bin")));

        Assert.Equal(100, first.TotalBytes);
        Assert.Empty(second.Matches);
    }

    [Fact]
    public void Broader_rule_excludes_paths_claimed_by_earlier_rules()
    {
        _dir.WriteFile("downloads/setup.exe", 700);
        _dir.WriteFile("downloads/app/cache/c.bin", 200);
        _dir.WriteFile("downloads/photo.jpg", 100);
        var evaluator = new RuleEvaluator(_drive);

        var installers = evaluator.Evaluate(Rule("installers", _dir.PathOf("downloads/*.exe")));
        var cache = evaluator.Evaluate(Rule("cache", _dir.PathOf("downloads/app/cache")));
        var downloads = evaluator.Evaluate(Rule("downloads", _dir.PathOf("downloads")));

        Assert.Equal(700, installers.TotalBytes);
        Assert.Equal(200, cache.TotalBytes);
        Assert.Equal(100, downloads.TotalBytes);
        Assert.Equal(1, downloads.FileCount);
    }

    [Fact]
    public void Nested_claims_are_subtracted_only_once()
    {
        _dir.WriteFile("a/b/c/inner.bin", 10);
        _dir.WriteFile("a/b/middle.bin", 20);
        _dir.WriteFile("a/outer.bin", 40);
        var evaluator = new RuleEvaluator(_drive);

        var inner = evaluator.Evaluate(Rule("inner", _dir.PathOf("a/b/c")));
        var middle = evaluator.Evaluate(Rule("middle", _dir.PathOf("a/b")));
        var outer = evaluator.Evaluate(Rule("outer", _dir.PathOf("a")));

        Assert.Equal([10L, 20L, 40L], new[] { inner.TotalBytes, middle.TotalBytes, outer.TotalBytes });
    }

    [Fact]
    public void Ignores_paths_on_other_drives()
    {
        _dir.WriteFile("cache/a.bin", 100);
        var otherDrive = _drive.StartsWith('Z') ? @"Y:\" : @"Z:\";

        var result = new RuleEvaluator(otherDrive).Evaluate(Rule("r", _dir.PathOf("cache")));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Reports_running_processes_regardless_of_exe_suffix_and_case()
    {
        _dir.WriteFile("cache/a.bin", 1);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cloudmusic", "explorer" };
        var rule = Rule("r", _dir.PathOf("cache")) with { Processes = ["CloudMusic.exe", "chrome"] };

        var result = new RuleEvaluator(_drive, running).Evaluate(rule);

        Assert.Equal(["cloudmusic"], result.RunningProcesses.Select(p => p.ToLowerInvariant()));
    }

    [Fact]
    public void Missing_paths_produce_no_matches()
    {
        var result = new RuleEvaluator(_drive).Evaluate(Rule("r", _dir.PathOf("missing")));

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.TotalBytes);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
