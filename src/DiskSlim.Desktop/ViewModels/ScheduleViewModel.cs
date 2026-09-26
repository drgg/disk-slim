using System.Collections.ObjectModel;
using System.Globalization;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scheduling;

namespace DiskSlim.Desktop.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public sealed record ScheduledRunViewModel(ScheduledRunRecord Record)
{
    public string TimeText => Record.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string Mark => Record.Success ? "✓" : "✗";

    public bool Success => Record.Success;

    public string Message => Record.Message;

    public string DetailText => Record.Files > 0
        ? $"清理 {ByteSize.Format(Record.CleanedBytes)} · 用时 {Record.ElapsedSeconds:0.#} 秒" + (Record.Failed > 0 ? $" · {Record.Failed:N0} 个文件正在使用，已跳过" : "")
        : $"用时 {Record.ElapsedSeconds:0.#} 秒";
}

/// <summary>“定时清理”页面：开启、修改、关闭定时清理，查看会清理什么和最近的运行记录。</summary>
public sealed class ScheduleViewModel : ObservableObject
{
    private readonly RuleSet _rules;
    private readonly ScheduleManager _manager;
    private ScheduleStatus? _status;
    private Choice<ScheduleFrequency> _frequency;
    private Choice<DayOfWeek> _day;
    private Choice<TimeOnly> _time;
    private string _statusText = "正在读取…";
    private string _statusDetail = "";
    private string _problem = "";
    private string _message = "";
    private bool _busy;

    /// <param name="manager">为 null 时使用默认位置和真实的 schtasks（测试时替换）。</param>
    /// <param name="programPath">定时任务要启动的程序，默认就是当前的 DiskSlimApp.exe。</param>
    public ScheduleViewModel(RuleSet rules, ScheduleManager? manager = null, string? programPath = null)
    {
        _rules = rules;
        _manager = manager ?? new ScheduleManager();
        ProgramPath = programPath ?? Environment.ProcessPath ?? "";
        Frequencies = [new(ScheduleFrequency.Weekly, "每周"), new(ScheduleFrequency.Daily, "每天")];
        Days = [.. Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7)).Select(d => new Choice<DayOfWeek>(d, ScheduleSettings.DayName(d)))];
        Times = [.. Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30)).Select(t => new Choice<TimeOnly>(t, t.ToString("HH:mm", CultureInfo.InvariantCulture)))];
        _frequency = Frequencies[0];
        _day = Days.Single(d => d.Value == DayOfWeek.Sunday);
        _time = Times.Single(t => t.Value == new TimeOnly(12, 0));
        AutomaticRules = [.. ScheduledCleanupJob.AutomaticRules(rules)
            .Select(r => r.MinAgeDays is > 0 && r.Action != CleanupAction.PurgeQuarantine ? $"{r.Name}（{r.MinAgeDays} 天前的文件）" : r.Name)];

        EnableCommand = new AsyncRelayCommand(EnableAsync, () => !_busy);
        DisableCommand = new AsyncRelayCommand(DisableAsync, () => !_busy && IsEnabled);
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => !_busy);
    }

    /// <summary>向用户确认，返回 true 才继续。由窗口提供（弹出确认框）。</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>“现在运行一次”完成后触发（主窗口据此重新扫描）。</summary>
    public event EventHandler? CleanupCompleted;

    public string ProgramPath { get; }

    public IReadOnlyList<Choice<ScheduleFrequency>> Frequencies { get; }

    public IReadOnlyList<Choice<DayOfWeek>> Days { get; }

    public IReadOnlyList<Choice<TimeOnly>> Times { get; }

    public IReadOnlyList<string> AutomaticRules { get; }

    public string AutomaticRulesTitle => $"会自动清理的项目（{AutomaticRules.Count} 项）";

    public ObservableCollection<ScheduledRunViewModel> Runs { get; } = [];

    public AsyncRelayCommand EnableCommand { get; }

    public AsyncRelayCommand DisableCommand { get; }

    public AsyncRelayCommand RunNowCommand { get; }

    public Choice<ScheduleFrequency> SelectedFrequency
    {
        get => _frequency;
        set
        {
            if (SetProperty(ref _frequency, value))
            {
                OnPropertyChanged(nameof(IsWeekly));
            }
        }
    }

    public bool IsWeekly => SelectedFrequency.Value == ScheduleFrequency.Weekly;

    public Choice<DayOfWeek> SelectedDay { get => _day; set => SetProperty(ref _day, value); }

    public Choice<TimeOnly> SelectedTime { get => _time; set => SetProperty(ref _time, value); }

    /// <summary>用户开启了定时清理（设置存在）。</summary>
    public bool IsEnabled => _status?.Settings is not null;

    public bool IsActive => _status?.IsActive == true;

    public string EnableButtonText => IsEnabled ? "保存设置" : "开启定时清理…";

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }

    public string Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => Problem.Length > 0;

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message.Length > 0;

    public bool HasRuns => Runs.Count > 0;

    public async Task RefreshAsync()
    {
        ScheduleStatus status;
        try
        {
            status = await _manager.GetStatusAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = "无法读取定时清理的状态";
            StatusDetail = ex.Message;
            return;
        }

        _status = status;
        if (status.Settings is { } settings)
        {
            SelectedFrequency = Frequencies.Single(f => f.Value == settings.Frequency);
            SelectedDay = Days.Single(d => d.Value == settings.Day);
            SelectedTime = Times.FirstOrDefault(t => t.Value == settings.Time) ?? SelectedTime;
            var next = settings.NextRun(DateTime.Now);
            StatusText = status.IsActive ? $"已开启 · {settings.Describe()}" : $"已开启，但现在不会运行 · {settings.Describe()}";
            StatusDetail = status.IsActive
                ? $"下次运行：{next:M月d日}（{ScheduleSettings.DayName(next.DayOfWeek)}）{next:HH:mm}"
                : "";
        }
        else
        {
            StatusText = "未开启";
            StatusDetail = "开启后会按时自动清理下面这些“可放心清理”的项目，不需要打开 DiskSlim。";
        }

        Problem = status.Problem ?? "";
        Runs.Clear();
        foreach (var run in _manager.RecentRuns(10))
        {
            Runs.Add(new ScheduledRunViewModel(run));
        }

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(EnableButtonText));
        OnPropertyChanged(nameof(HasRuns));
        DisableCommand.RaiseCanExecuteChanged();
    }

    private ScheduleSettings CurrentChoice() => new()
    {
        Program = ProgramPath,
        Frequency = SelectedFrequency.Value,
        Day = SelectedDay.Value,
        Time = SelectedTime.Value,
    };

    internal async Task EnableAsync()
    {
        var settings = CurrentChoice();
        if (!IsEnabled)
        {
            var question = $"开启定时清理：{settings.Describe()}，以你的身份（普通权限）在后台清理这 {AutomaticRules.Count} 项“可放心清理”的内容：\n\n"
                + string.Join("\n", AutomaticRules.Select(r => "· " + r))
                + "\n\n相关程序正在运行时跳过；“需要确认”的项目永远不会被自动清理；每次都会写操作日志。确定开启吗？";
            if (Confirm is { } confirm && !confirm(question))
            {
                return;
            }
        }

        await RunBusy(async () =>
        {
            var error = await _manager.EnableAsync(settings, DateTimeOffset.Now);
            Message = error ?? $"已{(IsEnabled ? "保存" : "开启")}：{settings.Describe()}。";
        });
    }

    internal async Task DisableAsync()
    {
        await RunBusy(async () =>
        {
            var error = await _manager.DisableAsync();
            Message = error ?? "已关闭定时清理。";
        });
    }

    internal async Task RunNowAsync()
    {
        var question = $"现在就按定时清理的方式清理这 {AutomaticRules.Count} 项“可放心清理”的内容吗？\n\n永久删除的内容无法恢复；相关程序正在运行的项目会跳过。";
        if (Confirm is { } confirm && !confirm(question))
        {
            return;
        }

        await RunBusy(async () =>
        {
            Message = "正在清理…";

            // 清理完主窗口会重新扫描并记下空间快照，这里不用再扫一遍。
            var job = new ScheduledCleanupJob(_rules, SystemInfo.SystemDriveRoot, _manager, history: null);
            var record = await Task.Run(() => job.Run());
            Message = (record.Success ? "✓ " : "✗ ") + record.Message;
            CleanupCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task RunBusy(Func<Task> action)
    {
        _busy = true;
        RaiseCommands();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Message = "操作失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        EnableCommand.RaiseCanExecuteChanged();
        DisableCommand.RaiseCanExecuteChanged();
        RunNowCommand.RaiseCanExecuteChanged();
    }
}
