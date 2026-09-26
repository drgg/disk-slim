using System.Collections.ObjectModel;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.History;
using DiskSlim.Core.Presentation;

namespace DiskSlim.Desktop.ViewModels;

/// <summary>趋势图上的一个点：一次扫描时的磁盘用量。</summary>
public sealed record UsagePoint(DateTimeOffset Time, long UsedBytes, long FreeBytes, bool HasDetails)
{
    public string Description => $"{Time.ToLocalTime():yyyy-MM-dd HH:mm} · 已用 {ByteSize.Format(UsedBytes)} · 剩余 {ByteSize.Format(FreeBytes)}"
        + (HasDetails ? "" : "（明细已按保留期限删除，不能比较）");
}

/// <summary>“和哪一次比较”下拉框中的一项。</summary>
public sealed record BaselineOption(SnapshotSummary Summary, string Label);

public sealed record GrowthItemViewModel(GrowthItem Item, long Threshold)
{
    public string Path => Item.Path;

    /// <summary>文件夹名；列表里加粗显示，完整路径太长时最有用的部分不会被截掉。</summary>
    public string Name => System.IO.Path.GetFileName(Item.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Item.Path;

    public string Location => System.IO.Path.GetDirectoryName(Item.Path.TrimEnd('\\')) ?? "";

    public string DeltaText => DisplayText.SignedSize(Item.Delta);

    public string DetailText => DisplayText.GrowthDetail(Item, Threshold);
}

public sealed record RuleChangeViewModel(RuleChange Change)
{
    public string Name => Change.Name;

    public string DeltaText => DisplayText.SignedSize(Change.Delta);

    public bool IsGrowth => Change.Delta > 0;

    public string DetailText => $"{ByteSize.Format(Change.OldBytes)} → {ByteSize.Format(Change.NewBytes)}";
}

/// <summary>“空间变化”页面：磁盘用量趋势，以及和之前某次扫描相比空间涨在哪里、减在哪里。</summary>
public sealed class GrowthViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultAge = TimeSpan.FromDays(7);

    private readonly SnapshotStore _store;
    private SpaceSnapshot? _current;
    private BaselineOption? _selectedBaseline;
    private IReadOnlyList<UsagePoint> _usage = [];
    private string _headline = "";
    private string _detail = "";
    private string _emptyText = "扫描完成后显示空间变化。";
    private string _caveat = "";
    private bool _hasComparison;

    public GrowthViewModel(SnapshotStore? store = null)
    {
        _store = store ?? SnapshotStore.Default;
    }

    public ObservableCollection<BaselineOption> Baselines { get; } = [];

    public ObservableCollection<GrowthItemViewModel> Grown { get; } = [];

    public ObservableCollection<GrowthItemViewModel> Shrunk { get; } = [];

    public ObservableCollection<RuleChangeViewModel> RuleChanges { get; } = [];

    /// <summary>趋势图的数据，按时间从旧到新，最后一个点是这次扫描。</summary>
    public IReadOnlyList<UsagePoint> Usage { get => _usage; private set => SetProperty(ref _usage, value); }

    public BaselineOption? SelectedBaseline
    {
        get => _selectedBaseline;
        set
        {
            if (SetProperty(ref _selectedBaseline, value))
            {
                OnPropertyChanged(nameof(BaselineTime));
                Compare();
            }
        }
    }

    /// <summary>当前比较对象的时间，趋势图用它标出基准点。</summary>
    public DateTimeOffset? BaselineTime => SelectedBaseline?.Summary.Time;

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>还不能比较时的说明。</summary>
    public string EmptyText { get => _emptyText; private set => SetProperty(ref _emptyText, value); }

    public string Caveat { get => _caveat; private set => SetProperty(ref _caveat, value); }

    public bool HasCaveat => Caveat.Length > 0;

    public bool HasComparison
    {
        get => _hasComparison;
        private set
        {
            if (SetProperty(ref _hasComparison, value))
            {
                OnPropertyChanged(nameof(HasNoComparison));
            }
        }
    }

    public bool HasNoComparison => !HasComparison;

    public bool HasRuleChanges => RuleChanges.Count > 0;

    public bool HasBaselines => Baselines.Count > 0;

    public bool HasNoGrown => HasComparison && Grown.Count == 0;

    public bool HasNoShrunk => HasComparison && Shrunk.Count == 0;

    /// <summary>
    /// 显示这次扫描和历史记录。<paramref name="history"/> 可以已经包含这次扫描（刚保存过），也可以不包含（没有保存）。
    /// </summary>
    /// <param name="saveError">保存快照失败的原因；为 null 表示保存成功或没有保存。</param>
    /// <param name="saved">这次扫描的快照是否已经保存（截图模式不保存）。</param>
    public void Show(SpaceSnapshot current, IReadOnlyList<SnapshotSummary> history, string? saveError = null, bool saved = true)
    {
        _current = current;
        var points = history
            .Where(h => h.Time != current.Time)
            .Select(h => new UsagePoint(h.Time, h.UsedBytes, h.FreeBytes, h.HasDetails))
            .Append(new UsagePoint(current.Time, current.UsedBytes, current.FreeBytes, HasDetails: true))
            .OrderBy(p => p.Time)
            .TakeLast(90)
            .ToList();
        Usage = points;

        Baselines.Clear();
        foreach (var entry in history.Where(h => h.HasDetails && h.Time < current.Time).OrderByDescending(h => h.Time))
        {
            Baselines.Add(new BaselineOption(entry, DisplayText.Since(entry.Time, current.Time)));
        }

        OnPropertyChanged(nameof(HasBaselines));
        var pick = GrowthAnalyzer.PickBaseline(history, current.Time, DefaultAge);
        _selectedBaseline = Baselines.FirstOrDefault(b => b.Summary.Time == pick?.Time);
        OnPropertyChanged(nameof(SelectedBaseline));
        OnPropertyChanged(nameof(BaselineTime));

        EmptyText = Baselines.Count == 0
            ? (saved ? "这是第一次扫描，已经记下了这次的空间快照。" : "还没有更早的扫描记录。")
              + "以后每次扫描都会记一份，那时这里会显示空间涨在哪里、减在哪里。"
            : "";
        if (saveError is not null)
        {
            EmptyText = (EmptyText.Length > 0 ? "这是第一次扫描。" : "") + "没能保存这次的空间快照：" + saveError;
        }

        Compare();
    }

    /// <summary>在趋势图上点了某个点：如果它有明细，就改为和它比较。</summary>
    public void SelectBaselineAt(DateTimeOffset time)
    {
        if (Baselines.FirstOrDefault(b => b.Summary.Time == time) is { } option)
        {
            SelectedBaseline = option;
        }
    }

    private void Compare()
    {
        Grown.Clear();
        Shrunk.Clear();
        RuleChanges.Clear();
        GrowthReport? report = null;
        if (_current is { } current && SelectedBaseline is { } baseline && _store.Load(current.Drive, baseline.Summary) is { } old)
        {
            report = GrowthAnalyzer.Compare(old, current, top: 20);
        }

        HasComparison = report is not null;
        if (report is null)
        {
            Headline = "";
            Detail = "";
            Caveat = "";
            if (SelectedBaseline is { } unreadable)
            {
                EmptyText = $"无法读取 {unreadable.Summary.Time.ToLocalTime():yyyy-MM-dd HH:mm} 的空间快照，文件可能已损坏。请在上面换一次扫描比较。";
            }
        }
        else
        {
            Headline = DisplayText.GrowthHeadline(report);
            Detail = $"扫描到的文件 {DisplayText.SignedSize(report.ScannedDelta)} · 只列出变化不小于 {ByteSize.Format(report.MinBytes)} 的位置，各项互不重叠";
            Caveat = DisplayText.GrowthCaveat(report) ?? "";
            var threshold = report.To.MinDirectoryBytes;
            foreach (var item in report.Grown)
            {
                Grown.Add(new GrowthItemViewModel(item, threshold));
            }

            foreach (var item in report.Shrunk)
            {
                Shrunk.Add(new GrowthItemViewModel(item, threshold));
            }

            foreach (var change in report.Rules?.Take(10) ?? [])
            {
                RuleChanges.Add(new RuleChangeViewModel(change));
            }
        }

        OnPropertyChanged(nameof(HasCaveat));
        OnPropertyChanged(nameof(HasRuleChanges));
        OnPropertyChanged(nameof(HasNoGrown));
        OnPropertyChanged(nameof(HasNoShrunk));
    }
}
