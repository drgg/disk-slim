using System.Collections.ObjectModel;
using DiskSlim.Core.Archives;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Desktop.ViewModels;

/// <summary>“已解压的压缩包”列表中的一个压缩包。</summary>
public sealed class ArchiveItemViewModel(ExtractedArchive archive) : ObservableObject
{
    private bool _isSelected = archive.SuggestRemoval;

    public event EventHandler? SelectionChanged;

    public ExtractedArchive Archive { get; } = archive;

    public string Path => Archive.Path;

    public string SizeText => ByteSize.Format(Archive.Bytes);

    public string Tag => DisplayText.ArchiveTag(Archive.Evidence);

    public ExtractionEvidence Evidence => Archive.Evidence;

    public string EvidenceText => DisplayText.ArchiveEvidence(Archive) + (Archive.Parts.Count > 1 ? $" · 共 {Archive.Parts.Count} 个分卷" : "");

    public string ExtractedToText => "解压在：" + Archive.ExtractedTo;

    /// <summary>没解压完的不能勾选；只按同名文件夹判断的可以勾选，但默认不勾，由用户自己确认。</summary>
    public bool CanSelect => Archive.Evidence != ExtractionEvidence.Partial;

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

/// <summary>“已解压的压缩包”页面：找出旁边已经有解压出来的文件的压缩包，勾选的移入隔离区。</summary>
public sealed class ArchivesViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private string _rootPath = "";
    private SizeOption _minSize;
    private bool _isSearching;
    private bool _purgeAfterRemove;
    private string _progressText = "";
    private string _summary = "点“开始查找”检查下载文件夹、桌面和各盘的网盘下载目录（也可以指定位置）。只读取文件，不会修改任何东西。";
    private string _message = "";
    private string _selectionText = "";
    private string? _lowSpaceWarning;

    public ArchivesViewModel()
    {
        _minSize = MinSizes[0];
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsSearching);
        BrowseCommand = new RelayCommand(Browse, () => !IsSearching);
        RemoveCommand = new RelayCommand(RemoveSelected, () => !IsSearching && SelectedItems.Any());
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
        new("50 MB", ArchiveSearchOptions.DefaultMinSize),
        new("10 MB", 10L << 20),
        new("200 MB", 200L << 20),
        new("1 GB", 1L << 30),
    ];

    public ObservableCollection<ArchiveItemViewModel> Items { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand SelectSuggestedCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>要查找的位置；留空时查默认位置。</summary>
    public string RootPath { get => _rootPath; set => SetProperty(ref _rootPath, value); }

    public SizeOption MinSize { get => _minSize; set => SetProperty(ref _minSize, value); }

    /// <summary>移入隔离区后立即彻底删除，马上释放空间（不能撤销）。默认不勾选。</summary>
    public bool PurgeAfterRemove
    {
        get => _purgeAfterRemove;
        set
        {
            if (SetProperty(ref _purgeAfterRemove, value))
            {
                UpdateSelection();
            }
        }
    }

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

    /// <summary>磁盘快满时提醒：移入隔离区并不能马上腾出空间。</summary>
    public string? LowSpaceWarning
    {
        get => _lowSpaceWarning;
        private set
        {
            if (SetProperty(ref _lowSpaceWarning, value))
            {
                OnPropertyChanged(nameof(HasLowSpaceWarning));
            }
        }
    }

    public bool HasLowSpaceWarning => LowSpaceWarning is not null;

    public IEnumerable<ArchiveItemViewModel> SelectedItems => Items.Where(i => i.IsSelected);

    public async Task SearchAsync()
    {
        IReadOnlyList<string> roots = string.IsNullOrWhiteSpace(RootPath) ? ArchiveSearchOptions.DefaultRoots() : [RootPath.Trim()];
        if (roots.Count == 0 || roots.Any(r => !Directory.Exists(r)))
        {
            Message = roots.Count == 0 ? "没有找到默认的下载文件夹，请指定要查找的位置。" : $"找不到文件夹：{RootPath}";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsSearching = true;
        Message = "";
        var finder = new ExtractedArchiveFinder();
        var options = ArchiveSearchOptions.ForCurrentUser(MinSize.Bytes);
        try
        {
            var task = Task.Run(() => finder.Find(roots, options, cts.Token), cts.Token);
            while (!task.IsCompleted)
            {
                ProgressText = $"{finder.Stage} · 已检查 {finder.ArchivesChecked:N0} 个压缩包";
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
    internal void Show(ArchiveSearchResult result)
    {
        foreach (var item in Items)
        {
            item.SelectionChanged -= OnSelectionChanged;
        }

        Items.Clear();
        foreach (var archive in result.Archives)
        {
            var item = new ArchiveItemViewModel(archive);
            item.SelectionChanged += OnSelectionChanged;
            Items.Add(item);
        }

        var where = string.Join("、", result.Roots);
        Summary = result.Archives.Count == 0
            ? $"没有找到已经解压过的压缩包（在 {where} 里检查了 {result.ArchivesChecked:N0} 个，用时 {result.ElapsedSeconds:0.0} 秒）。"
            : $"找到 {result.Archives.Count:N0} 个看起来已经解压过的压缩包，其中逐个核对过、可以放心移除的有 {ByteSize.Format(result.SuggestedBytes)}"
              + $"（在 {where} 里检查了 {result.ArchivesChecked:N0} 个，用时 {result.ElapsedSeconds:0.0} 秒）。";
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
        foreach (var item in Items)
        {
            item.IsSelected = suggestedOnly && item.Archive.SuggestRemoval;
        }
    }

    private void RemoveSelected()
    {
        var selected = SelectedItems.Select(i => i.Archive).ToList();
        var bytes = selected.Sum(a => a.Bytes);
        var nameOnly = selected.Count(a => !a.SuggestRemoval);
        var question = $"把选中的 {selected.Count:N0} 个压缩包（{ByteSize.Format(bytes)}）移入隔离区？\n\n解压出来的文件不受影响。"
            + (nameOnly > 0 ? $"\n\n其中 {nameOnly:N0} 个是 rar、7z 这类读不出文件列表的压缩包，只按同名文件夹判断，请确认你已经不需要它们。" : "")
            + (PurgeAfterRemove
                ? "\n\n移入后会立即彻底删除，不能撤销。"
                : "\n\n隔离区里的文件可以随时恢复，彻底删除后才会释放空间。");
        if (Confirm is { } confirm && !confirm(question))
        {
            return;
        }

        Message = Remove(selected);
        QuarantineChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>移入隔离区（勾选了立即删除时再彻底删除），从列表里去掉已经移走的压缩包，返回给用户看的结果。</summary>
    internal string Remove(IReadOnlyList<ExtractedArchive> archives, Func<string, QuarantineStore>? storeFor = null, string? logPath = null)
    {
        storeFor ??= QuarantineStore.ForDrive;
        ArchiveRemovalResult result;
        long purgedBytes = 0;
        string? purgeError = null;
        try
        {
            using var log = new JsonLinesOperationLog(logPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now));
            result = new ExtractedArchiveRemover(storeFor, SafetyGuard.ForCurrentUser(), log).Remove(archives, dryRun: false);
            if (PurgeAfterRemove)
            {
                foreach (var batch in result.Batches)
                {
                    var store = storeFor(batch.DriveRoot);
                    if (store.Find(batch.SessionId) is { } session)
                    {
                        var purge = store.Purge([session], log);
                        purgedBytes += purge.PurgedBytes;
                        purgeError ??= purge.Problems.FirstOrDefault()?.Reason;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "没有移动任何文件：无法创建操作日志（" + ex.Message + "）";
        }

        foreach (var item in Items.Where(i => !File.Exists(i.Path)).ToList())
        {
            item.SelectionChanged -= OnSelectionChanged;
            Items.Remove(item);
        }

        UpdateSelection();
        var message = PurgeAfterRemove && purgeError is null
            ? $"已彻底删除 {result.Files:N0} 个文件，释放了 {ByteSize.Format(purgedBytes)}。"
            : $"已把 {result.Files:N0} 个文件（{ByteSize.Format(result.Bytes)}）移入隔离区，可以在“隔离区”页面恢复。"
              + (purgeError is not null ? $"立即彻底删除没有成功：{purgeError}。" : "");
        return message + (result.Skipped.Count > 0 ? $"{result.Skipped.Count:N0} 个没有移动：{result.Skipped[0].Reason}" : "");
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = SelectedItems.ToList();
        SelectionText = selected.Count == 0
            ? "没有勾选要移除的压缩包"
            : $"已勾选 {selected.Count:N0} 个压缩包 · 可省 {ByteSize.Format(selected.Sum(i => i.Archive.Bytes))}";
        LowSpaceWarning = PurgeAfterRemove
            ? null
            : selected
                .GroupBy(i => System.IO.Path.GetPathRoot(i.Path)!, StringComparer.OrdinalIgnoreCase)
                .Select(g => DisplayText.QuarantineLowSpaceWarning(g.Key, g.Sum(i => i.Archive.Bytes)))
                .OfType<string>()
                .FirstOrDefault() is { } warning ? warning + "可以勾选“移入后立即彻底删除”。" : null;
        RemoveCommand.RaiseCanExecuteChanged();
    }
}
