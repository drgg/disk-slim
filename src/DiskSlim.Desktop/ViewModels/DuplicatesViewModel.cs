using System.Collections.ObjectModel;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Duplicates;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;

namespace DiskSlim.Desktop.ViewModels;

public sealed class DuplicateFileViewModel(DuplicateFile file, bool isKeeper, bool isSuggested) : ObservableObject
{
    private bool _isSelected = isSuggested;

    public event EventHandler? SelectionChanged;

    public DuplicateFile File { get; } = file;

    public string Path => File.Path;

    public string DateText => File.CreationTimeUtc.ToLocalTime().ToString("yyyy-MM-dd");

    public bool IsProtected => File.IsProtected;

    public bool CanSelect => !File.IsProtected;

    public string? Tag => File.IsProtected ? "受保护" : isKeeper ? "建议保留" : File.LooksLikeCopy ? "明显的副本" : null;

    public bool HasTag => Tag is not null;

    public string? Note => File.ProtectedReason;

    public bool HasNote => Note is not null;

    /// <summary>勾选 = 移入隔离区。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value && CanSelect))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

public sealed class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        Group = group;
        var keeper = group.SuggestedKeeper;
        var suggested = group.SuggestedRemovals;
        foreach (var file in group.Files.OrderBy(f => ReferenceEquals(f, keeper) ? 0 : 1))
        {
            var item = new DuplicateFileViewModel(file, ReferenceEquals(file, keeper), suggested.Contains(file));
            item.SelectionChanged += OnFileSelectionChanged;
            Files.Add(item);
        }
    }

    public event EventHandler? SelectionChanged;

    public DuplicateGroup Group { get; }

    public ObservableCollection<DuplicateFileViewModel> Files { get; } = [];

    public string Header => $"{ByteSize.Format(Group.FileSize)} × {Group.Files.Count} 份 · 只保留一份可省 {ByteSize.Format(Group.WastedBytes)}";

    public string? Note => Group.HasProtectedFiles ? "组里有受保护的文件（聊天记录、备份、程序库……），默认全部保留。" : null;

    public bool HasNote => Note is not null;

    public IEnumerable<DuplicateFileViewModel> Selected => Files.Where(f => f.IsSelected);

    /// <summary>勾选了“最后一份”时自动取消，保证每组至少保留一份。</summary>
    public string? LastWarning { get; private set; }

    private void OnFileSelectionChanged(object? sender, EventArgs e)
    {
        LastWarning = null;
        if (sender is DuplicateFileViewModel file && file.IsSelected && Files.All(f => f.IsSelected || !f.CanSelect) && !Files.Any(f => f.IsProtected))
        {
            file.IsSelected = false;
            LastWarning = "每组至少要保留一份，不能全部勾选。";
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record SizeOption(string Label, long Bytes);

/// <summary>“重复文件”页面。</summary>
public sealed class DuplicatesViewModel : ObservableObject
{
    public const int MaxGroupsShown = 300;

    private readonly RuleSet _rules;
    private CancellationTokenSource? _cts;
    private string _rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private SizeOption _minSize;
    private bool _isSearching;
    private string _progressText = "";
    private string _summary = "选择要查找的位置，然后点“开始查找”。只读取文件，不会修改任何东西。";
    private string _message = "";
    private string _selectionText = "";

    public DuplicatesViewModel(RuleSet rules)
    {
        _rules = rules;
        _minSize = MinSizes[0];
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsSearching);
        BrowseCommand = new RelayCommand(Browse, () => !IsSearching);
        RemoveCommand = new RelayCommand(RemoveSelected, () => !IsSearching && SelectedFiles.Any());
        SelectSuggestedCommand = new RelayCommand(() => SetSelection(suggestedOnly: true));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(suggestedOnly: false));
        UpdateSelection();
    }

    /// <summary>移入隔离区之后触发，主窗口据此刷新隔离区页面。</summary>
    public event EventHandler? QuarantineChanged;

    /// <summary>选择文件夹（窗口提供），返回 null 表示取消。</summary>
    public Func<string, string?>? PickFolder { get; set; }

    /// <summary>移入隔离区前确认（窗口提供）。</summary>
    public Func<string, bool>? Confirm { get; set; }

    public IReadOnlyList<SizeOption> MinSizes { get; } =
    [
        new("1 MB", 1L << 20),
        new("10 MB", 10L << 20),
        new("100 MB", 100L << 20),
        new("100 KB", 100L << 10),
    ];

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand SelectSuggestedCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public string RootPath { get => _rootPath; set => SetProperty(ref _rootPath, value); }

    public SizeOption MinSize { get => _minSize; set => SetProperty(ref _minSize, value); }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                SearchCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                BrowseCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }

    public IEnumerable<DuplicateFileViewModel> SelectedFiles => Groups.SelectMany(g => g.Selected);

    public async Task SearchAsync()
    {
        if (!Directory.Exists(RootPath))
        {
            Message = $"找不到文件夹：{RootPath}";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsSearching = true;
        Message = "";
        var finder = new DuplicateFinder();
        var options = DuplicateSearchOptions.ForCurrentUser(_rules, MinSize.Bytes);
        var root = RootPath;
        try
        {
            var task = Task.Run(() => finder.Find([root], options, cts.Token), cts.Token);
            while (!task.IsCompleted)
            {
                ProgressText = $"{finder.Stage} · {finder.FilesScanned:N0} 个文件 · 已读取 {ByteSize.Format(finder.BytesHashed)}";
                await Task.WhenAny(task, Task.Delay(150));
            }

            Show(await task);
        }
        catch (OperationCanceledException)
        {
            Summary = "已取消查找。";
        }
        finally
        {
            _cts = null;
            ProgressText = "";
            IsSearching = false;
        }
    }

    /// <summary>显示查找结果。</summary>
    internal void Show(DuplicateSearchResult result)
    {
        foreach (var group in Groups)
        {
            group.SelectionChanged -= OnSelectionChanged;
        }

        Groups.Clear();
        foreach (var group in result.Groups.Take(MaxGroupsShown))
        {
            var item = new DuplicateGroupViewModel(group);
            item.SelectionChanged += OnSelectionChanged;
            Groups.Add(item);
        }

        Summary = result.Groups.Count == 0
            ? $"没有找到重复文件（查了 {result.FilesScanned:N0} 个文件，用时 {result.ElapsedSeconds:0.0} 秒）。"
            : $"找到 {result.Groups.Count:N0} 组重复文件，只保留一份可以省下 {ByteSize.Format(result.WastedBytes)}（查了 {result.FilesScanned:N0} 个文件，用时 {result.ElapsedSeconds:0.0} 秒）"
              + (result.Groups.Count > MaxGroupsShown ? $"。只列出最大的 {MaxGroupsShown} 组。" : "。");
        UpdateSelection();
    }

    private void Browse()
    {
        if (PickFolder?.Invoke(RootPath) is { } folder)
        {
            RootPath = folder;
        }
    }

    private void SetSelection(bool suggestedOnly)
    {
        foreach (var group in Groups)
        {
            var suggested = group.Group.SuggestedRemovals;
            foreach (var file in group.Files)
            {
                file.IsSelected = suggestedOnly && suggested.Contains(file.File);
            }
        }
    }

    private void RemoveSelected()
    {
        var selections = Groups
            .Where(g => g.Selected.Any())
            .Select(g => new DuplicateSelection(g.Group, g.Selected.Select(f => f.File).ToList()))
            .ToList();
        var count = selections.Sum(s => s.Remove.Count);
        var bytes = selections.Sum(s => s.Remove.Sum(f => f.Bytes));
        var question = $"把选中的 {count:N0} 个副本（{ByteSize.Format(bytes)}）移入隔离区？\n\n每组至少保留一份。隔离区里的文件可以随时恢复，彻底删除后才会释放空间。";
        if (Confirm is { } confirm && !confirm(question))
        {
            return;
        }

        Message = Remove(selections);
        QuarantineChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>移入隔离区，从列表里去掉已经移走的文件，返回给用户看的结果。</summary>
    internal string Remove(IReadOnlyList<DuplicateSelection> selections, Func<string, QuarantineStore>? storeFor = null, string? logPath = null)
    {
        storeFor ??= QuarantineStore.ForDrive;
        var guard = SafetyGuard.ForCurrentUser();
        var results = new List<DuplicateRemovalResult>();
        try
        {
            using var log = new JsonLinesOperationLog(logPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now));
            foreach (var drive in selections.GroupBy(s => Path.GetPathRoot(s.Group.Files[0].Path)!, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new DuplicateRemover(storeFor(drive.Key), guard, log).Remove(drive.ToList(), dryRun: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "没有移动任何文件：无法创建操作日志（" + ex.Message + "）";
        }

        // 从列表里去掉已经移走的文件；只剩一份的组也去掉。
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            foreach (var file in selection.Remove.Where(f => !System.IO.File.Exists(f.Path)))
            {
                moved.Add(file.Path);
            }
        }

        foreach (var group in Groups.ToList())
        {
            foreach (var file in group.Files.Where(f => moved.Contains(f.Path)).ToList())
            {
                group.Files.Remove(file);
            }

            if (group.Files.Count < 2)
            {
                group.SelectionChanged -= OnSelectionChanged;
                Groups.Remove(group);
            }
        }

        UpdateSelection();
        var skipped = results.Sum(r => r.Skipped.Count);
        return $"已把 {results.Sum(r => r.Files):N0} 个副本（{ByteSize.Format(results.Sum(r => r.Bytes))}）移入隔离区，可以在“隔离区”页面恢复。"
            + (skipped > 0 ? $"{skipped:N0} 个没有移动：{results.SelectMany(r => r.Skipped).First().Reason}" : "");
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is DuplicateGroupViewModel { LastWarning: { } warning })
        {
            Message = warning;
        }

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = SelectedFiles.ToList();
        SelectionText = selected.Count == 0
            ? "没有勾选要移除的副本"
            : $"已勾选 {selected.Count:N0} 个副本 · 可省 {ByteSize.Format(selected.Sum(f => f.File.Bytes))}";
        RemoveCommand.RaiseCanExecuteChanged();
    }
}
