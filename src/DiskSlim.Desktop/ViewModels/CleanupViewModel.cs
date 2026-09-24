using System.Collections.ObjectModel;
using System.Diagnostics;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Rules;

namespace DiskSlim.Desktop.ViewModels;

public enum CleanupStage
{
    /// <summary>正在生成计划并模拟运行。</summary>
    Preparing,

    /// <summary>等待用户确认。</summary>
    Confirm,

    Running,

    Done,

    Error,
}

/// <summary>清理对话框中的一条规则。</summary>
public sealed class CleanupItemViewModel(string name, RiskLevel risk, string status, OutcomeKind kind, string? details) : ObservableObject
{
    public string Name { get; } = name;

    public string RiskText { get; } = DisplayText.RiskShort(risk);

    public string Status { get; } = status;

    public OutcomeKind Kind { get; } = kind;

    public string? Details { get; } = details;

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    public static CleanupItemViewModel From(RuleOutcome outcome, bool executed)
    {
        var (status, kind) = executed ? DisplayText.ResultSummary(outcome) : DisplayText.PlanSummary(outcome);
        var details = new[]
            {
                DisplayText.SkipSummary(outcome),
                DisplayText.FailureSummary(outcome, executed),
                outcome.Planned.Problems.Count > 0 ? string.Join("\n", outcome.Planned.Problems) : null,
            }
            .Where(s => s is not null);
        return new CleanupItemViewModel(outcome.Rule.Name, outcome.Rule.Risk, status, kind, string.Join("\n", details));
    }
}

/// <summary>
/// 清理对话框：先模拟并列出计划，用户点“开始清理”后才执行。和命令行共用同一套计划和执行逻辑。
/// </summary>
public sealed class CleanupViewModel : ObservableObject
{
    private readonly RuleSet _rules;
    private readonly string _driveRoot;
    private readonly IReadOnlyList<string> _ruleIds;
    private readonly IFileOperations? _operations;
    private readonly string? _logPath;
    private CleanupPlan? _plan;
    private CancellationTokenSource? _runCts;
    private CleanupStage _stage = CleanupStage.Preparing;
    private string _title = "正在生成清理计划…";
    private string _headline = "正在检查选中的项目，还不会删除任何文件。";
    private string _progressText = "";
    private string _footerText = "";
    private string? _logFilePath;

    public CleanupViewModel(RuleSet rules, string driveRoot, IReadOnlyList<string> ruleIds, IFileOperations? operations = null, string? logPath = null)
    {
        _rules = rules;
        _driveRoot = driveRoot;
        _ruleIds = ruleIds;
        _operations = operations;
        _logPath = logPath;
        StartCommand = new AsyncRelayCommand(StartAsync, () => Stage == CleanupStage.Confirm);
        CancelCommand = new RelayCommand(Cancel);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => LogFilePath is not null);
    }

    /// <summary>对话框应该关闭。</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<CleanupItemViewModel> Items { get; } = [];

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>是否真正执行过清理（对话框关闭后主窗口据此重新扫描）。</summary>
    public bool HasExecuted { get; private set; }

    public CleanupResult? Result { get; private set; }

    public CleanupStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(IsConfirm));
                OnPropertyChanged(nameof(CancelText));
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsWorking => Stage is CleanupStage.Preparing or CleanupStage.Running;

    public bool IsConfirm => Stage == CleanupStage.Confirm;

    public string CancelText => Stage switch
    {
        CleanupStage.Running => "停止",
        CleanupStage.Done or CleanupStage.Error => "关闭",
        _ => "取消",
    };

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string FooterText { get => _footerText; private set => SetProperty(ref _footerText, value); }

    public string? LogFilePath
    {
        get => _logFilePath;
        private set
        {
            if (SetProperty(ref _logFilePath, value))
            {
                OnPropertyChanged(nameof(HasLog));
                OpenLogFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasLog => LogFilePath is not null;

    /// <summary>生成计划并模拟运行。</summary>
    public async Task PrepareAsync()
    {
        try
        {
            var running = SystemInfo.RunningProcessNames();
            _plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), running).Plan(_rules, _driveRoot, _ruleIds);
        }
        catch (CleanupSelectionException ex)
        {
            Fail("无法清理选中的项目", ex.Message);
            return;
        }

        var simulator = new CleanupExecutor(_operations);
        var preview = await Track(Task.Run(() => simulator.Execute(_plan, dryRun: true)), () => $"正在检查：{simulator.CurrentRule} · {simulator.FilesVisited:N0} 个文件");

        Items.Clear();
        foreach (var outcome in preview.Rules)
        {
            Items.Add(CleanupItemViewModel.From(outcome, executed: false));
        }

        if (preview.DeletedFiles + preview.RecycledFiles == 0)
        {
            Title = "没有需要清理的内容";
            Headline = "选中的项目里没有满足条件的文件（可能程序正在运行、文件太新，或者需要管理员权限）。";
            Stage = CleanupStage.Done;
            return;
        }

        Title = "清理计划（模拟运行）";
        Headline = DisplayText.ConfirmSummary(preview);
        FooterText = "以上是模拟结果，还没有删除任何文件。正在被使用的文件会在执行时自动跳过。";
        Stage = CleanupStage.Confirm;
    }

    /// <summary>真正执行清理（由“开始清理”按钮调用，只在确认阶段可用）。</summary>
    internal async Task StartAsync()
    {
        if (Stage != CleanupStage.Confirm)
        {
            return;
        }

        if (_plan is null)
        {
            return;
        }

        JsonLinesOperationLog log;
        var logPath = _logPath ?? JsonLinesOperationLog.DefaultPath(DateTimeOffset.Now);
        try
        {
            log = new JsonLinesOperationLog(logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("没有执行清理", $"无法创建操作日志 {logPath}：{ex.Message}。为安全起见，没有删除任何文件。");
            return;
        }

        Stage = CleanupStage.Running;
        Title = "正在清理…";
        Headline = "可以随时点“停止”，处理完当前文件后就会停下。";
        FooterText = "";
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        CleanupResult result;
        using (log)
        {
            var executor = new CleanupExecutor(_operations, log);
            result = await Track(Task.Run(() => executor.Execute(_plan, dryRun: false, cts.Token)), () => $"{executor.CurrentRule} · 已处理 {ByteSize.Format(executor.BytesCleaned)}");
        }

        _runCts = null;
        Result = result;
        HasExecuted = true;
        LogFilePath = result.LogPath;

        Items.Clear();
        foreach (var outcome in result.Rules)
        {
            Items.Add(CleanupItemViewModel.From(outcome, executed: true));
        }

        Title = result.Cancelled ? "已停止" : "清理完成";
        Headline = DisplayText.FreeSpaceChange(result)
            + (result.RecycledBytes > 0 ? $"。另有 {ByteSize.Format(result.RecycledBytes)} 移到了回收站，清空回收站后才会释放。" : "。");
        FooterText = $"操作日志：{result.LogPath}";
        Stage = CleanupStage.Done;
    }

    private void Cancel()
    {
        if (Stage == CleanupStage.Running)
        {
            _runCts?.Cancel();
            Headline = "正在停止…";
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLogFolder()
    {
        if (LogFilePath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }

    private void Fail(string title, string message)
    {
        Title = title;
        Headline = message;
        Stage = CleanupStage.Error;
    }

    /// <summary>等待后台任务，期间定时刷新进度文字。</summary>
    private async Task<T> Track<T>(Task<T> task, Func<string> describe)
    {
        while (!task.IsCompleted)
        {
            ProgressText = describe();
            await Task.WhenAny(task, Task.Delay(150));
        }

        ProgressText = "";
        return await task;
    }
}
