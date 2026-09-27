using System.Collections.ObjectModel;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.SystemTasks;

namespace DiskSlim.Desktop.ViewModels;

public sealed class SystemTaskItemViewModel(SystemTask task) : ObservableObject
{
    private string _estimateText = "正在估算…";
    private string _status = "";
    private bool _isRunning;
    private bool _notNeeded;
    private bool _needsRestart;
    private bool _isSizeEstimate;

    public SystemTask Task { get; } = task;

    public string Name => Task.Name;

    public string Description => Task.Description;

    public string Warning => "注意：" + Task.Warning;

    public string? UndoHint => Task.UndoHint;

    public bool HasUndoHint => UndoHint is not null;

    public string EstimateText { get => _estimateText; set => SetProperty(ref _estimateText, value); }

    /// <summary>估算出了具体大小（界面上用绿色显示）；否则只是说明文字。</summary>
    public bool IsSizeEstimate { get => _isSizeEstimate; set => SetProperty(ref _isSizeEstimate, value); }

    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }

    public bool NotNeeded
    {
        get => _notNeeded;
        set
        {
            if (SetProperty(ref _notNeeded, value))
            {
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    /// <summary>现在运行一定会失败，要先重启电脑（界面上用警告色显示，不给“运行”按钮）。</summary>
    public bool NeedsRestart
    {
        get => _needsRestart;
        set
        {
            if (SetProperty(ref _needsRestart, value))
            {
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public bool CanRun => !NotNeeded && !NeedsRestart;
}

/// <summary>
/// “系统清理”页面：每项任务都通过以管理员身份运行的 diskslim.exe 助手执行（会弹出 UAC 提示）。
/// </summary>
public sealed class SystemTasksViewModel : ObservableObject
{
    private bool _busy;

    public SystemTasksViewModel()
    {
        foreach (var task in SystemTaskCatalog.All)
        {
            Items.Add(new SystemTaskItemViewModel(task));
        }

        RunTaskCommand = new RelayCommand(p => _ = RunAsync(p as SystemTaskItemViewModel), _ => !_busy);
    }

    /// <summary>执行前确认（窗口提供）。</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>找到 diskslim.exe（窗口可以替换，测试时使用）。</summary>
    public Func<string?> FindHelper { get; set; } = FindHelperExecutable;

    /// <summary>有任务执行完成（主窗口据此重新扫描）。</summary>
    public event EventHandler? TaskCompleted;

    public ObservableCollection<SystemTaskItemViewModel> Items { get; } = [];

    public RelayCommand RunTaskCommand { get; }

    public string ElevationNote => SystemInfo.IsElevated()
        ? "当前已经是管理员权限。"
        : "当前不是管理员权限：点“运行”后会弹出 Windows 的 UAC 提示，同意后才会执行。";

    /// <summary>重新估算每项任务能释放多少空间（只读）。</summary>
    public async Task RefreshEstimatesAsync()
    {
        foreach (var item in Items)
        {
            var estimate = await Task.Run(() => SystemTaskCatalog.Estimate(item.Task));
            item.NotNeeded = estimate.NotNeeded;
            item.NeedsRestart = estimate.NeedsRestart;
            item.IsSizeEstimate = !estimate.NotNeeded && estimate.Bytes is > 0;
            item.EstimateText = estimate.NotNeeded ? estimate.Note
                : estimate.Bytes is { } bytes ? $"约可释放 {ByteSize.Format(bytes)}" + (estimate.Note.Length > 0 ? $"（{estimate.Note}）" : "")
                : estimate.Note;
        }
    }

    private async Task RunAsync(SystemTaskItemViewModel? item)
    {
        if (item is null || _busy)
        {
            return;
        }

        var question = $"{item.Name}\n\n{item.Description}\n\n注意：{item.Task.Warning}" + (item.UndoHint is { } undo ? $"\n\n{undo}" : "")
            + "\n\n确定运行吗？" + (SystemInfo.IsElevated() ? "" : "接下来会弹出 UAC 提示请求管理员权限。");
        if (Confirm is { } confirm && !confirm(question))
        {
            return;
        }

        if (FindHelper() is not { } helper)
        {
            item.Status = "找不到 diskslim.exe：请把它和 DiskSlimApp.exe 放在同一个文件夹里。";
            return;
        }

        _busy = true;
        RunTaskCommand.RaiseCanExecuteChanged();
        item.IsRunning = true;
        item.Status = "正在以管理员身份运行，可能需要几分钟，请不要关机……";
        try
        {
            var result = await ElevatedHelper.RunAsync(helper, item.Task.Id, JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now));
            item.Status = result is null ? "你在 UAC 提示里选择了“否”，没有做任何改动。" : Describe(result);
            if (result is { Success: true })
            {
                TaskCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            item.Status = "运行失败：" + ex.Message;
        }
        finally
        {
            item.IsRunning = false;
            _busy = false;
            RunTaskCommand.RaiseCanExecuteChanged();
            await RefreshEstimatesAsync();
        }
    }

    internal static string Describe(SystemTaskResult result)
    {
        var text = (result.Success ? "✓ " : "✗ ") + result.Message;
        if (result.FreeBytesAfter > 0)
        {
            var freed = result.FreeBytesAfter - result.FreeBytesBefore;
            text += $" 磁盘剩余空间 {ByteSize.Format(result.FreeBytesBefore)} → {ByteSize.Format(result.FreeBytesAfter)}（{(freed >= 0 ? "增加" : "减少")} {ByteSize.Format(Math.Abs(freed))}）。";
        }

        return text;
    }

    /// <summary>发布后 diskslim.exe 和 DiskSlimApp.exe 在同一个文件夹；开发时在命令行项目的输出目录里。</summary>
    private static string? FindHelperExecutable()
    {
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "diskslim.exe");
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cli = Path.Combine(dir.FullName, "DiskSlim.Cli", "bin");
            if (Directory.Exists(cli))
            {
                return Directory.EnumerateFiles(cli, "diskslim.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        return null;
    }
}
