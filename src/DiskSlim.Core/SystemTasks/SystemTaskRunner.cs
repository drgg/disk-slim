using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.SystemTasks;

public sealed record CommandResult(int ExitCode, string Output);

/// <summary>运行系统程序。集中在这里，便于审查，也便于测试时替换成假的实现。</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments);
}

public interface IServiceControl
{
    bool IsRunning(string serviceName);

    void Stop(string serviceName, TimeSpan timeout);

    void Start(string serviceName, TimeSpan timeout);
}

/// <summary>一次系统任务的结果；助手进程把它写成 JSON 交回给调用方。</summary>
public sealed record SystemTaskResult
{
    public required string TaskId { get; init; }

    public required bool Success { get; init; }

    public string Message { get; init; } = "";

    public int? ExitCode { get; init; }

    /// <summary>程序输出的最后若干行。</summary>
    public string Output { get; init; } = "";

    public long FreeBytesBefore { get; init; }

    public long FreeBytesAfter { get; init; }

    public double ElapsedSeconds { get; init; }

    public bool RebootRequired { get; init; }
}

/// <summary>
/// 执行系统级清理任务。只能运行 <see cref="SystemTaskCatalog"/> 里的任务，每项任务做什么写死在这里；
/// 系统程序一律用 System32 下的完整路径调用，避免被同名程序冒充。需要管理员权限。
/// </summary>
/// <param name="updateDownloadDirectory">Windows 更新下载缓存的位置，只在测试时替换。</param>
public sealed class SystemTaskRunner(
    ICommandRunner? commands = null,
    IServiceControl? services = null,
    IOperationLog? log = null,
    Func<bool>? isElevated = null,
    string? updateDownloadDirectory = null)
{
    private const int RebootRequiredExitCode = 3010;
    private static readonly string[] UpdateServices = ["wuauserv", "bits"];

    private readonly ICommandRunner _commands = commands ?? new ProcessCommandRunner();
    private readonly IServiceControl _services = services ?? new WindowsServiceControl();
    private readonly IOperationLog _log = log ?? NullOperationLog.Instance;
    private readonly Func<bool> _isElevated = isElevated ?? SystemInfo.IsElevated;
    private readonly string _updateDownloadDirectory = updateDownloadDirectory ?? SystemTaskCatalog.UpdateDownloadDirectory;

    public static string SystemProgram(string name) => Path.Combine(Environment.SystemDirectory, name);

    public async Task<SystemTaskResult> RunAsync(string taskId)
    {
        if (SystemTaskCatalog.Find(taskId) is not { } task)
        {
            return new SystemTaskResult { TaskId = taskId, Success = false, Message = $"没有这项系统任务：{taskId}" };
        }

        if (!_isElevated())
        {
            return new SystemTaskResult { TaskId = task.Id, Success = false, Message = "需要管理员权限才能运行这项任务。" };
        }

        var systemDrive = Path.GetPathRoot(SystemTaskCatalog.WindowsDirectory)!;
        var freeBefore = FreeSpace(systemDrive);
        var stopwatch = Stopwatch.StartNew();
        Log("run-start", task.Id, $"系统任务：{task.Name}");

        SystemTaskResult result;
        try
        {
            result = task.Id switch
            {
                SystemTaskCatalog.ComponentStore => await RunProgram(task, "dism.exe", ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]),
                SystemTaskCatalog.DeliveryOptimization => await RunProgram(task, @"WindowsPowerShell\v1.0\powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "Delete-DeliveryOptimizationCache -Force"]),
                SystemTaskCatalog.HibernationOff => await RunProgram(task, "powercfg.exe", ["/hibernate", "off"]),
                SystemTaskCatalog.WindowsUpdateCache => ClearUpdateCache(task),
                _ => new SystemTaskResult { TaskId = task.Id, Success = false, Message = "这项任务还没有实现。" },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new SystemTaskResult { TaskId = task.Id, Success = false, Message = "运行失败：" + ex.Message };
        }

        var freeAfter = FreeSpace(systemDrive);
        result = result with { FreeBytesBefore = freeBefore, FreeBytesAfter = freeAfter, ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1) };
        Log("run-end", task.Id, $"{(result.Success ? "成功" : "失败")}：{result.Message}", freeAfter - freeBefore);
        return result;
    }

    private async Task<SystemTaskResult> RunProgram(SystemTask task, string program, IReadOnlyList<string> arguments)
    {
        var path = SystemProgram(program);
        Log("command", task.Id, $"{path} {string.Join(" ", arguments)}");
        var output = await _commands.RunAsync(path, arguments);
        var success = output.ExitCode is 0 or RebootRequiredExitCode;
        Log(success ? "command-done" : "failed", task.Id, $"退出码 {output.ExitCode}");
        return new SystemTaskResult
        {
            TaskId = task.Id,
            Success = success,
            ExitCode = output.ExitCode,
            Output = Tail(output.Output),
            RebootRequired = output.ExitCode == RebootRequiredExitCode,
            Message = success
                ? output.ExitCode == RebootRequiredExitCode ? "完成，重启电脑后生效。" : "完成。"
                : $"{program} 返回了错误码 {output.ExitCode}，详情见输出。",
        };
    }

    /// <summary>暂停更新服务 → 删除下载缓存 → 恢复服务。不论删除成功与否，原来在运行的服务都会被重新启动。</summary>
    private SystemTaskResult ClearUpdateCache(SystemTask task)
    {
        var wasRunning = UpdateServices.Where(_services.IsRunning).ToList();
        try
        {
            foreach (var service in wasRunning)
            {
                Log("service-stop", task.Id, service);
                _services.Stop(service, TimeSpan.FromSeconds(60));
            }

            // 用和普通清理完全一样的计划和执行器：同样的安全防线、跳过规则和日志。
            var rule = new CleanupRule
            {
                Id = task.Id,
                Name = task.Name,
                Risk = RiskLevel.Low,
                Action = CleanupAction.Delete,
                Paths = [_updateDownloadDirectory],
            };
            var drive = Path.GetPathRoot(_updateDownloadDirectory)!;
            var plan = new CleanupPlanner(SafetyGuard.ForCurrentUser(), new HashSet<string>()).Plan(new RuleSet { Rules = [rule] }, drive, [rule.Id]);
            var outcome = new CleanupExecutor(log: _log).Execute(plan, dryRun: false).Rules.Single();

            return new SystemTaskResult
            {
                TaskId = task.Id,
                Success = outcome.Planned.Status is PlanStatus.Ready or PlanStatus.NothingFound,
                Message = Presentation.DisplayText.ResultSummary(outcome).Text,
            };
        }
        finally
        {
            foreach (var service in wasRunning)
            {
                try
                {
                    Log("service-start", task.Id, service);
                    _services.Start(service, TimeSpan.FromSeconds(60));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
                {
                    Log("failed", task.Id, $"无法重新启动服务 {service}：{ex.Message}（重启电脑后会自动恢复）");
                }
            }
        }
    }

    private void Log(string evt, string taskId, string detail, long? bytes = null) =>
        _log.Write(new LogEntry { Time = DateTimeOffset.Now, Event = evt, Rule = taskId, Detail = detail, Bytes = bytes });

    /// <summary>保留最后 40 行，去掉 DISM 进度条这类反复刷新的行。</summary>
    private static string Tail(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n', '\r')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0 && !l.TrimStart().StartsWith('['))
            .TakeLast(40);
        return string.Join(Environment.NewLine, lines);
    }

    private static long FreeSpace(string drive)
    {
        try
        {
            return new DriveInfo(drive).TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        // 系统程序按控制台代码页（中文系统是 GBK）输出。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // 不支持中途取消：强行结束 DISM 这类系统维护程序可能留下不一致的状态。
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, (await stdout) + (await stderr));
    }
}

public sealed class WindowsServiceControl : IServiceControl
{
    public bool IsRunning(string serviceName)
    {
        using var service = new ServiceController(serviceName);
        return service.Status == ServiceControllerStatus.Running;
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }
    }
}
