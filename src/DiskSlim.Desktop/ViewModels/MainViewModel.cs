using System.Collections.ObjectModel;
using System.Diagnostics;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.History;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scheduling;
using DiskSlim.Core.Scanning;
using DiskSlim.Core.Visualization;

namespace DiskSlim.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly RuleSet _rules;
    private readonly SnapshotStore _history;
    private CancellationTokenSource? _scanCts;
    private DriveOption? _selectedDrive;
    private bool _isScanning;
    private string _progressText = "";
    private string _statusText = "准备就绪";
    private string _driveTitle = "";
    private string _driveDetail = "";
    private double _usedRatio;
    private DirNode? _mapRoot;
    private DirNode? _mapCurrent;
    private CleanableIndex _index = CleanableIndex.Empty;
    private string _hoverText = "把鼠标移到方块上查看详情，点击方块进入该文件夹。";
    private string _currentTitle = "";
    private string _currentDetail = "";
    private string _selectionText = "";
    private IReadOnlyList<string> _notes = [];

    /// <param name="history">空间快照的存放位置；为 null 时使用默认位置（测试时替换）。</param>
    /// <param name="schedule">定时清理的设置和任务注册；为 null 时使用默认位置（测试时替换）。</param>
    public MainViewModel(RuleSet rules, SnapshotStore? history = null, ScheduleManager? schedule = null)
    {
        _rules = rules;
        _history = history ?? SnapshotStore.Default;
        Growth = new GrowthViewModel(_history);
        Schedule = new ScheduleViewModel(rules, schedule);
        IsElevated = SystemInfo.IsElevated();
        Duplicates = new DuplicatesViewModel(rules);
        Duplicates.QuarantineChanged += (_, _) => Quarantine.Refresh();
        Archives.QuarantineChanged += (_, _) => Quarantine.Refresh();
        foreach (var drive in DriveOption.Available(SystemInfo.SystemDriveRoot))
        {
            Drives.Add(drive);
        }

        _selectedDrive = Drives.FirstOrDefault(d => d.IsSystem) ?? Drives.FirstOrDefault();

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && SelectedDrive is not null);
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        CleanCommand = new RelayCommand(RequestCleanup, () => !IsScanning && SelectedItems.Any());
        NavigateUpCommand = new RelayCommand(() => Navigate(MapCurrent?.Parent), () => MapCurrent?.Parent is not null);
        NavigateToCommand = new RelayCommand(p => Navigate(p as DirNode));
        RestartAsAdminCommand = new RelayCommand(() => RestartAsAdminRequested?.Invoke(this, EventArgs.Empty), () => !IsElevated);
    }

    /// <summary>用户确认要清理时触发，由窗口弹出清理对话框。</summary>
    public event EventHandler<CleanupViewModel>? CleanupRequested;

    public event EventHandler? RestartAsAdminRequested;

    public ObservableCollection<DriveOption> Drives { get; } = [];

    public ObservableCollection<RuleGroupViewModel> Groups { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<FolderItemViewModel> CurrentChildren { get; } = [];

    public ObservableCollection<LargeFileViewModel> LargestFiles { get; } = [];

    public QuarantineViewModel Quarantine { get; } = new();

    public DuplicatesViewModel Duplicates { get; }

    public ArchivesViewModel Archives { get; } = new();

    public SystemTasksViewModel SystemTasks { get; } = new();

    public GrowthViewModel Growth { get; }

    public ScheduleViewModel Schedule { get; }

    /// <summary>扫描后是否把空间快照保存下来（截图模式不保存）。</summary>
    public bool SaveHistory { get; set; } = true;

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand CleanCommand { get; }

    public RelayCommand NavigateUpCommand { get; }

    public RelayCommand NavigateToCommand { get; }

    public RelayCommand RestartAsAdminCommand { get; }

    public bool IsElevated { get; }

    public DriveOption? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value) && value is not null && !IsScanning)
            {
                ScanCommand.Execute(null);
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                ScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                CleanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsScanning;

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string DriveTitle { get => _driveTitle; private set => SetProperty(ref _driveTitle, value); }

    public string DriveDetail { get => _driveDetail; private set => SetProperty(ref _driveDetail, value); }

    public double UsedRatio { get => _usedRatio; private set => SetProperty(ref _usedRatio, value); }

    public IReadOnlyList<string> Notes { get => _notes; private set => SetProperty(ref _notes, value); }

    public DirNode? MapRoot { get => _mapRoot; private set => SetProperty(ref _mapRoot, value); }

    public DirNode? MapCurrent { get => _mapCurrent; private set => SetProperty(ref _mapCurrent, value); }

    public CleanableIndex Index { get => _index; private set => SetProperty(ref _index, value); }

    public string HoverText { get => _hoverText; set => SetProperty(ref _hoverText, value); }

    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    public string CurrentDetail { get => _currentDetail; private set => SetProperty(ref _currentDetail, value); }

    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }

    public IEnumerable<RuleItemViewModel> SelectedItems => Groups.SelectMany(g => g.Items).Where(i => i.IsSelected);

    public async Task ScanAsync()
    {
        if (SelectedDrive is not { } drive)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsScanning = true;
        StatusText = $"正在扫描 {drive.Root}";
        var builder = new DiskReportBuilder();
        var options = new ReportOptions { DriveRoot = drive.Root, TopCount = 50, Rules = _rules };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var task = Task.Run(() => builder.Build(options, cts.Token), cts.Token);
            while (!task.IsCompleted)
            {
                ProgressText = DescribeProgress(builder);
                await Task.WhenAny(task, Task.Delay(150));
            }

            var report = await task;
            Apply(report, builder.MapResult);
            if (builder.MapResult is { } map)
            {
                await RecordHistoryAsync(report, map);
            }

            Quarantine.Load(drive.Root);
            _ = SystemTasks.RefreshEstimatesAsync();
            _ = Schedule.RefreshAsync();
            StatusText = $"扫描完成，用时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒。" + (IsElevated ? "" : "当前不是管理员权限，部分系统目录无法读取。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消扫描。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanCts = null;
            IsScanning = false;
        }
    }

    /// <summary>记下这次扫描的空间快照（保存失败不影响扫描结果），并更新“空间变化”页面。</summary>
    internal async Task RecordHistoryAsync(ScanReport report, ScanResult map)
    {
        SpaceSnapshot snapshot;
        try
        {
            snapshot = SpaceSnapshot.Create(report, map);
        }
        catch (ArgumentException)
        {
            return;
        }

        var save = SaveHistory;
        var (history, error) = await Task.Run(() =>
        {
            string? saveError = null;
            if (save)
            {
                try
                {
                    _history.Save(snapshot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    saveError = ex.Message;
                }
            }

            return (_history.History(snapshot.Drive), saveError);
        });
        Growth.Show(snapshot, history, error, saved: save && error is null);
    }

    /// <summary>把报告和扫描树展示到界面上。</summary>
    internal void Apply(ScanReport report, ScanResult? map)
    {
        var d = report.Drive;
        DriveTitle = string.IsNullOrEmpty(d.Label) ? d.Root : $"{d.Root}  {d.Label}";
        DriveDetail = $"{d.Format} · 总容量 {ByteSize.Format(d.TotalBytes)} · 已用 {ByteSize.Format(d.UsedBytes)}（{d.UsedRatio * 100:0}%） · 剩余 {ByteSize.Format(d.FreeBytes)}";
        UsedRatio = d.UsedRatio;
        Notes = report.Notes;

        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            item.SelectionChanged -= OnSelectionChanged;
        }

        Groups.Clear();
        foreach (var risk in new[] { RiskLevel.Low, RiskLevel.Medium, RiskLevel.High })
        {
            var items = report.Rules
                .Where(r => r.Rule.Risk == risk)
                .Select(r => new RuleItemViewModel(r))
                .OrderByDescending(i => i.CountedBytes)
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            foreach (var item in items)
            {
                item.SelectionChanged += OnSelectionChanged;
            }

            Groups.Add(new RuleGroupViewModel(risk, items));
        }

        UpdateSelection();

        LargestFiles.Clear();
        foreach (var file in report.SpaceMap?.LargestFiles ?? [])
        {
            LargestFiles.Add(new LargeFileViewModel(file.Path, file.Bytes, file.LastWriteTimeUtc));
        }

        if (map is not null)
        {
            Index = CleanableIndex.Build(map.Root, report.Rules);
            MapRoot = map.Root;
            Navigate(map.Root);
        }
    }

    public void Navigate(DirNode? node)
    {
        if (node is null)
        {
            return;
        }

        MapCurrent = node;

        Breadcrumbs.Clear();
        var chain = new List<DirNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            chain.Insert(0, n);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            Breadcrumbs.Add(new BreadcrumbItem(chain[i].Name, chain[i], i == chain.Count - 1));
        }

        var path = node.FullPath;
        var total = Math.Max(1, node.TotalBytes);
        var children = node.Children
            .Where(c => c.TotalBytes > 0)
            .Select(c => new FolderItemViewModel(c.Name, System.IO.Path.Join(path, c.Name), c.TotalBytes, (double)c.TotalBytes / total, Index.CleanableBytes(c), c))
            .ToList();
        if (node.OwnBytes > 0)
        {
            children.Add(new FolderItemViewModel("（本目录中的文件）", path, node.OwnBytes, (double)node.OwnBytes / total, 0, null));
        }

        CurrentChildren.Clear();
        foreach (var child in children.OrderByDescending(c => c.Bytes).Take(300))
        {
            CurrentChildren.Add(child);
        }

        CurrentTitle = path;
        var cleanable = Index.CleanableBytes(node);
        CurrentDetail = $"{ByteSize.Format(node.TotalBytes)} · {node.TotalFileCount:N0} 个文件"
            + (cleanable > 0 ? $" · 其中可清理 {ByteSize.Format(cleanable)}" : "")
            + (node.TotalInaccessibleCount > 0 ? $" · {node.TotalInaccessibleCount:N0} 个文件夹无权限读取" : "");
        NavigateUpCommand.RaiseCanExecuteChanged();
    }

    /// <summary>方块图上悬停的方块发生变化。</summary>
    public void DescribeTile(TreemapTile? tile)
    {
        if (tile is null || MapCurrent is null)
        {
            HoverText = "把鼠标移到方块上查看详情，点击方块进入该文件夹。";
            return;
        }

        var share = MapCurrent.TotalBytes == 0 ? 0 : (double)tile.Bytes / MapCurrent.TotalBytes;
        var name = tile.Kind switch
        {
            TileKind.OwnFiles => $"{tile.Path} 中的文件",
            TileKind.Other => $"{tile.Path} 中较小的 {tile.ItemCount:N0} 项",
            _ => tile.Path,
        };
        var text = $"{name} · {ByteSize.Format(tile.Bytes)}（占当前目录 {share * 100:0.#}%）";
        if (tile.CleanableBytes > 0)
        {
            text += $" · 可清理 {ByteSize.Format(tile.CleanableBytes)}";
        }

        if (tile.Risk is { } risk)
        {
            text += $" · {Core.Presentation.DisplayText.RiskTitle(risk)}";
        }

        HoverText = text;
    }

    /// <summary>为当前勾选的项目创建清理对话框的视图模型（还不会执行任何操作）。</summary>
    internal CleanupViewModel? CreateCleanup() => SelectedDrive is { } drive
        ? new CleanupViewModel(_rules, drive.Root, SelectedItems.Select(i => i.Id).ToList())
        : null;

    private void RequestCleanup()
    {
        if (CreateCleanup() is { } cleanup)
        {
            CleanupRequested?.Invoke(this, cleanup);
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = SelectedItems.ToList();
        var blocked = selected.Where(i => i.Result.RunningProcesses.Count > 0).ToList();
        var expected = selected.Except(blocked).Sum(i => i.CountedBytes);
        SelectionText = selected.Count == 0
            ? "没有选中任何项目"
            : $"已选 {selected.Count} 项 · 预计释放 {ByteSize.Format(expected)}"
              + (blocked.Count > 0 ? $"（另有 {ByteSize.Format(blocked.Sum(i => i.CountedBytes))} 因程序正在运行会被跳过）" : "");
        CleanCommand.RaiseCanExecuteChanged();
    }

    private static string DescribeProgress(DiskReportBuilder builder) => builder.MapScanner is { } scanner
        ? $"{builder.Stage} · {scanner.FilesScanned:N0} 个文件 · {ByteSize.Format(scanner.BytesScanned)}"
        : builder.Stage;
}
