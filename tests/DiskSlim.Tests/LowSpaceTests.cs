using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

/// <summary>磁盘快满时：“移入隔离区”“移到回收站”不能马上腾出空间，要提醒，并且可以选择清理后立即彻底删除。</summary>
public sealed class LowSpaceTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly QuarantineStore _store;
    private readonly string _drive;

    public LowSpaceTests()
    {
        _store = new QuarantineStore(_dir.PathOf("quarantine"));
        _drive = Path.GetPathRoot(_dir.Root)!;
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(8L << 30, 149L << 30, true)]    // 这台电脑的 C 盘：149 GB 只剩 8 GB
    [InlineData(8L << 30, 391L << 30, true)]    // D 盘：391 GB 只剩 8 GB
    [InlineData(104L << 30, 390L << 30, false)] // E 盘：还剩 104 GB
    [InlineData(25L << 30, 1000L << 30, false)] // 大硬盘：最多按 20 GB 算
    [InlineData(1L << 30, 16L << 30, true)]     // 小 U 盘：少于 10%
    [InlineData(1L << 30, 0L, false)]           // 读不到容量
    public void Low_space_means_less_than_ten_percent_capped_at_twenty_gigabytes(long free, long total, bool low)
    {
        Assert.Equal(low, DiskSpace.IsLow(free, total));
    }

    [Fact]
    public void Warning_explains_that_quarantine_and_recycle_bin_do_not_free_space_yet()
    {
        var preview = Preview(free: 8L << 30, total: 149L << 30, (CleanupAction.Quarantine, 2L << 30), (CleanupAction.Recycle, 1L << 30), (CleanupAction.Delete, 5L << 30));

        var warning = DisplayText.LowSpaceWarning(preview);

        Assert.NotNull(warning);
        Assert.StartsWith("磁盘只剩 8.00 GB", warning);
        Assert.Contains("移入隔离区的 2.00 GB 要彻底删除后才会腾出空间", warning);
        Assert.Contains("移到回收站的 1.00 GB", warning);

        // 选择立即彻底删除后，只剩回收站需要提醒。
        Assert.DoesNotContain("隔离区", DisplayText.LowSpaceWarning(preview, purgeQuarantine: true));
    }

    [Fact]
    public void No_warning_when_there_is_enough_space_or_everything_is_deleted_for_good()
    {
        Assert.Null(DisplayText.LowSpaceWarning(Preview(free: 104L << 30, total: 390L << 30, (CleanupAction.Quarantine, 2L << 30))));
        Assert.Null(DisplayText.LowSpaceWarning(Preview(free: 8L << 30, total: 149L << 30, (CleanupAction.Delete, 2L << 30))));
    }

    [Fact]
    public void Purge_now_deletes_the_new_batch_right_after_cleaning()
    {
        _dir.WriteFile("models/big.bin", 8192, Old);
        var rule = new CleanupRule { Id = "models", Name = "模型缓存", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("models")] };
        var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>(), _store).Plan(new RuleSet { Rules = [rule] }, _drive, ["models"]);

        var preview = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: true, purgeQuarantine: true);
        Assert.Contains("会在清理后立即彻底删除", DisplayText.ConfirmSummary(preview, purgeQuarantine: true));
        Assert.True(File.Exists(_dir.PathOf("models/big.bin"))); // 模拟运行什么都不动

        var result = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: false, purgeQuarantine: true);

        Assert.True(result.QuarantinePurged);
        Assert.Null(result.QuarantineSessionId); // 不能再撤销
        Assert.Empty(_store.ListSessions());
        Assert.False(File.Exists(_dir.PathOf("models/big.bin")));
        Assert.StartsWith("已彻底删除 1 个文件", DisplayText.ResultSummary(result.Rules.Single()).Text);
        Assert.Null(DisplayText.PendingSpaceNote(result));
    }

    [Fact]
    public void Without_purge_now_the_batch_stays_restorable()
    {
        _dir.WriteFile("models/big.bin", 8192, Old);
        var rule = new CleanupRule { Id = "models", Name = "模型缓存", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("models")] };
        var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>(), _store).Plan(new RuleSet { Rules = [rule] }, _drive, ["models"]);

        var result = new CleanupExecutor(quarantine: _store).Execute(plan, dryRun: false);

        Assert.False(result.QuarantinePurged);
        Assert.NotNull(result.QuarantineSessionId);
        Assert.Single(_store.ListSessions());
        Assert.Contains("移入了隔离区", DisplayText.PendingSpaceNote(result));
    }

    [Fact]
    public async Task Cleanup_dialog_offers_to_purge_right_away()
    {
        _dir.WriteFile("models/big.bin", 8192, Old);
        var rules = new RuleSet
        {
            Rules = [new CleanupRule { Id = "models", Name = "模型缓存", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("models")] }],
        };
        var cleanup = new Desktop.ViewModels.CleanupViewModel(rules, _drive, ["models"], logPath: _dir.PathOf("logs/q.jsonl"), quarantine: _store);

        await cleanup.PrepareAsync();
        Assert.True(cleanup.ShowPurgeOption);
        Assert.False(cleanup.PurgeAfterCleanup); // 默认不勾选：保留撤销的机会

        cleanup.PurgeAfterCleanup = true;
        Assert.Contains("立即彻底删除", cleanup.Headline);

        await cleanup.StartAsync();

        Assert.False(cleanup.CanUndoQuarantine);
        Assert.Empty(_store.ListSessions());
        Assert.False(cleanup.ShowPurgeOption);
    }

    /// <summary>按指定的清理方式和大小构造一个模拟结果。</summary>
    private static CleanupResult Preview(long free, long total, params (CleanupAction Action, long Bytes)[] items) => new()
    {
        DryRun = true,
        FreeBytesBefore = free,
        FreeBytesAfter = free,
        DriveTotalBytes = total,
        Rules = items.Select((item, i) =>
        {
            var rule = new CleanupRule { Id = $"r{i}", Name = $"规则{i}", Risk = RiskLevel.Medium, Action = item.Action, Paths = ["x"] };
            var outcome = new RuleOutcome(new PlannedRule { Rule = rule, Status = PlanStatus.Ready }) { Files = 1, Bytes = item.Bytes };
            return outcome;
        }).ToList(),
    };
}
