using System.Collections.ObjectModel;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Desktop.ViewModels;

public sealed record QuarantineSessionViewModel(QuarantineSession Session)
{
    public const int ExpiryDays = 7;

    public string Id => Session.Id;

    public string Description => string.IsNullOrWhiteSpace(Session.Description) ? "（没有说明）" : Session.Description;

    public string SizeText => ByteSize.Format(Session.TotalBytes);

    public double AgeDays => (DateTimeOffset.Now - Session.CreatedAt).TotalDays;

    public bool IsExpired => AgeDays >= ExpiryDays;

    public string DetailText => $"{Session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 移入 · {Session.Items.Count:N0} 个文件 · "
        + (AgeDays < 1 ? "今天" : $"已放 {AgeDays:0} 天") + (IsExpired ? "，可以彻底删除了" : "");
}

/// <summary>“隔离区”页面：查看、恢复、彻底删除。</summary>
public sealed class QuarantineViewModel : ObservableObject
{
    private QuarantineStore? _store;
    private string _summary = "";
    private string _message = "";

    public QuarantineViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        RestoreCommand = new RelayCommand(p => Restore(p as QuarantineSessionViewModel));
        PurgeCommand = new RelayCommand(p => Purge(p as QuarantineSessionViewModel));
    }

    /// <summary>彻底删除前向用户确认，返回 true 才执行。由窗口提供（弹出确认框）。</summary>
    public Func<string, bool>? ConfirmPurge { get; set; }

    public ObservableCollection<QuarantineSessionViewModel> Sessions { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand PurgeCommand { get; }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>最近一次操作的结果。</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public bool HasSessions => Sessions.Count > 0;

    public string? Location => _store?.Root;

    /// <summary>切换到指定磁盘的隔离区。</summary>
    public void Load(string driveRoot)
    {
        _store = QuarantineStore.ForDrive(driveRoot);
        OnPropertyChanged(nameof(Location));
        Refresh();
    }

    public void Refresh()
    {
        Sessions.Clear();
        foreach (var session in _store?.ListSessions() ?? [])
        {
            Sessions.Add(new QuarantineSessionViewModel(session));
        }

        var total = Sessions.Sum(s => s.Session.TotalBytes);
        Summary = Sessions.Count == 0
            ? "隔离区是空的"
            : $"共 {Sessions.Count} 批 · {ByteSize.Format(total)} · 这些文件还占着空间，彻底删除后才会释放";
        OnPropertyChanged(nameof(HasSessions));
    }

    private void Restore(QuarantineSessionViewModel? item)
    {
        if (_store is null || item is null)
        {
            return;
        }

        var result = _store.Restore(item.Session, SafetyGuard.ForCurrentUser());
        Message = $"已恢复 {result.Restored:N0} 个文件（{ByteSize.Format(result.RestoredBytes)}）到原来的位置。"
            + (result.Problems.Count > 0 ? $"{result.Problems.Count:N0} 个没有恢复（原位置已有同名文件等），仍在隔离区中。" : "");
        Refresh();
    }

    private void Purge(QuarantineSessionViewModel? item)
    {
        if (_store is null || item is null)
        {
            return;
        }

        var question = $"彻底删除这一批（{item.Session.Items.Count:N0} 个文件，{item.SizeText}）？\n\n{item.Description}\n\n删除后无法恢复。";
        if (ConfirmPurge is { } confirm && !confirm(question))
        {
            return;
        }

        PurgeResult result;
        try
        {
            using var log = new JsonLinesOperationLog(JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now));
            result = _store.Purge([item.Session], log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "没有删除：无法创建操作日志（" + ex.Message + "）";
            return;
        }

        Message = result.Problems.Count == 0
            ? $"已彻底删除，释放了 {ByteSize.Format(result.PurgedBytes)}。"
            : "删除失败：" + result.Problems[0].Reason;
        Refresh();
    }
}
