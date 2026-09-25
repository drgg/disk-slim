using DiskSlim.Core.Presentation;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Tests;

public sealed class DesktopViewModelTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly string _drive;

    public DesktopViewModelTests() => _drive = Path.GetPathRoot(_dir.Root)!;

    public void Dispose() => _dir.Dispose();

    private static RuleResult Result(RiskLevel risk, CleanupAction action, long bytes, params string[] running) => new()
    {
        Rule = new CleanupRule { Id = $"{risk}-{action}", Name = "测试", Risk = risk, Action = action, Paths = ["x"] },
        Matches = [new RuleMatch("x", bytes, bytes, 1, 0)],
        RunningProcesses = running,
    };

    [Theory]
    [InlineData(RiskLevel.Low, CleanupAction.Delete, true, true)]
    [InlineData(RiskLevel.Medium, CleanupAction.Delete, true, false)] // 需要确认：能选，但默认不选
    [InlineData(RiskLevel.Low, CleanupAction.None, false, false)]     // 没有清理方式
    [InlineData(RiskLevel.High, CleanupAction.None, false, false)]    // 仅供参考
    public void Selection_defaults_match_the_command_line(RiskLevel risk, CleanupAction action, bool selectable, bool selectedByDefault)
    {
        var item = new RuleItemViewModel(Result(risk, action, 100));

        Assert.Equal(selectable, item.IsSelectable);
        Assert.Equal(selectedByDefault, item.IsSelected);
    }

    [Fact]
    public void Items_that_cannot_be_cleaned_cannot_be_ticked()
    {
        var item = new RuleItemViewModel(Result(RiskLevel.High, CleanupAction.None, 100));

        item.IsSelected = true;

        Assert.False(item.IsSelected);
        Assert.Contains("仅供参考", item.ActionText);
    }

    [Fact]
    public void Selection_summary_excludes_items_whose_programs_are_running()
    {
        var viewModel = new MainViewModel(new RuleSet { Rules = [] });
        var report = new ScanReport
        {
            ToolVersion = "test",
            GeneratedAt = DateTimeOffset.Now,
            IsElevated = false,
            Drive = new DriveSummary(_drive, "", "NTFS", 1000, 500),
            RulesChecked = true,
            Rules =
            [
                Result(RiskLevel.Low, CleanupAction.Delete, 1024) with { Rule = new CleanupRule { Id = "a", Name = "A", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = ["x"] } },
                Result(RiskLevel.Low, CleanupAction.Delete, 2048, "cloudmusic") with { Rule = new CleanupRule { Id = "b", Name = "B", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = ["y"] } },
                Result(RiskLevel.Medium, CleanupAction.Delete, 4096),
            ],
        };

        viewModel.Apply(report, map: null);

        Assert.Equal([RiskLevel.Low, RiskLevel.Medium], viewModel.Groups.Select(g => g.Risk));
        Assert.Equal("已选 2 项 · 预计释放 1.00 KB（另有 2.00 KB 因程序正在运行会被跳过）", viewModel.SelectionText);

        viewModel.Groups[1].Items[0].IsSelected = true;
        Assert.StartsWith("已选 3 项 · 预计释放 5.00 KB", viewModel.SelectionText);
    }

    [Fact]
    public async Task Cleanup_dialog_simulates_first_and_only_deletes_after_start()
    {
        _dir.WriteFile("cache/junk.tmp", 4096, Old);
        _dir.WriteFile("chat/history.db", 100, Old);
        var rules = new RuleSet
        {
            Rules =
            [
                new CleanupRule { Id = "chat", Name = "聊天记录", Risk = RiskLevel.High, Paths = [_dir.PathOf("chat")] },
                new CleanupRule { Id = "cache", Name = "缓存", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = [_dir.PathOf("cache")] },
            ],
        };
        var logPath = _dir.PathOf("logs/gui.jsonl");
        var cleanup = new CleanupViewModel(rules, _drive, ["cache"], logPath: logPath);

        await cleanup.PrepareAsync();

        Assert.Equal(CleanupStage.Confirm, cleanup.Stage);
        Assert.True(cleanup.StartCommand.CanExecute(null));
        Assert.Contains("永久删除 1 项", cleanup.Headline);
        Assert.Equal(OutcomeKind.Done, Assert.Single(cleanup.Items).Kind);
        Assert.True(File.Exists(_dir.PathOf("cache/junk.tmp"))); // 模拟阶段什么都没删
        Assert.False(File.Exists(logPath));

        await cleanup.StartAsync();

        Assert.Equal(CleanupStage.Done, cleanup.Stage);
        Assert.True(cleanup.HasExecuted);
        Assert.False(File.Exists(_dir.PathOf("cache/junk.tmp")));
        Assert.True(File.Exists(_dir.PathOf("chat/history.db")));
        Assert.Equal(Path.GetFullPath(logPath), cleanup.LogFilePath);
        Assert.StartsWith("已删除 1 个文件", cleanup.Items.Single().Status);
    }

    [Fact]
    public async Task Cleanup_dialog_can_undo_what_it_moved_into_quarantine()
    {
        _dir.WriteFile("models/big.bin", 8192, Old);
        var store = new Core.Quarantine.QuarantineStore(_dir.PathOf("quarantine"));
        var rules = new RuleSet
        {
            Rules = [new CleanupRule { Id = "models", Name = "模型缓存", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("models")] }],
        };
        var cleanup = new CleanupViewModel(rules, _drive, ["models"], logPath: _dir.PathOf("logs/q.jsonl"), quarantine: store);

        await cleanup.PrepareAsync();
        Assert.Contains("移入隔离区", cleanup.Headline);
        Assert.False(cleanup.CanUndoQuarantine);

        await cleanup.StartAsync();
        Assert.False(File.Exists(_dir.PathOf("models/big.bin")));
        Assert.True(cleanup.CanUndoQuarantine);
        Assert.Contains("彻底删除后才释放空间", cleanup.Headline);

        cleanup.UndoQuarantine();

        Assert.Equal(8192, new FileInfo(_dir.PathOf("models/big.bin")).Length);
        Assert.False(cleanup.CanUndoQuarantine);
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public async Task Cleanup_dialog_reports_when_there_is_nothing_to_do()
    {
        _dir.WriteFile("cache/fresh.tmp", 10);
        var rules = new RuleSet
        {
            Rules = [new CleanupRule { Id = "cache", Name = "缓存", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = [_dir.PathOf("cache")], MinAgeDays = 1 }],
        };
        var cleanup = new CleanupViewModel(rules, _drive, ["cache"]);

        await cleanup.PrepareAsync();

        Assert.Equal(CleanupStage.Done, cleanup.Stage);
        Assert.False(cleanup.StartCommand.CanExecute(null));
        await cleanup.StartAsync(); // 不在确认阶段，什么都不做
        Assert.False(cleanup.HasExecuted);
        Assert.True(File.Exists(_dir.PathOf("cache/fresh.tmp")));
    }

    [Fact]
    public async Task Cleanup_dialog_refuses_high_risk_selection()
    {
        var rules = new RuleSet { Rules = [new CleanupRule { Id = "chat", Name = "聊天记录", Risk = RiskLevel.High, Paths = [_dir.PathOf("chat")] }] };
        var cleanup = new CleanupViewModel(rules, _drive, ["chat"]);

        await cleanup.PrepareAsync();

        Assert.Equal(CleanupStage.Error, cleanup.Stage);
        Assert.Contains("仅供参考", cleanup.Headline);
    }
}
