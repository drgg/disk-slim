using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Rules;
using DiskSlim.Core.SystemTasks;

namespace DiskSlim.Tests;

/// <summary>
/// 系统任务的测试全部使用假的命令执行和服务控制：不会真的运行 DISM、powercfg，也不会停止任何服务。
/// </summary>
public sealed class SystemTaskTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly FakeCommands _commands = new();
    private readonly FakeServices _services = new();
    private readonly MemoryLog _log = new();

    public void Dispose() => _dir.Dispose();

    private SystemTaskRunner Runner(bool elevated = true) =>
        new(_commands, _services, _log, () => elevated, _dir.PathOf("SoftwareDistribution/Download"));

    [Fact]
    public void Catalog_is_a_fixed_allowlist()
    {
        Assert.Equal(["component-store", "windows-update-cache", "delivery-optimization", "hibernation-off"], SystemTaskCatalog.All.Select(t => t.Id));
        Assert.Null(SystemTaskCatalog.Find("format-c"));
        Assert.Equal("hibernation-off", SystemTaskCatalog.ForRule("hibernation-file")!.Id);
    }

    [Fact]
    public async Task Refuses_unknown_tasks_and_non_admin_runs()
    {
        var unknown = await Runner().RunAsync("cmd /c del *");
        var notAdmin = await Runner(elevated: false).RunAsync("component-store");

        Assert.False(unknown.Success);
        Assert.False(notAdmin.Success);
        Assert.Contains("管理员", notAdmin.Message);
        Assert.Empty(_commands.Calls);
    }

    [Theory]
    [InlineData("component-store", "dism.exe", "/Online /Cleanup-Image /StartComponentCleanup")]
    [InlineData("hibernation-off", "powercfg.exe", "/hibernate off")]
    [InlineData("delivery-optimization", "powershell.exe", "-NoProfile -NonInteractive -Command Delete-DeliveryOptimizationCache -Force")]
    public async Task Runs_exactly_the_fixed_system_command_by_full_path(string taskId, string program, string arguments)
    {
        var result = await Runner().RunAsync(taskId);

        Assert.True(result.Success);
        var (file, args) = Assert.Single(_commands.Calls);
        Assert.StartsWith(Environment.SystemDirectory, file, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(program, file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(arguments, string.Join(" ", args));
    }

    [Fact]
    public async Task Component_cleanup_never_uses_reset_base()
    {
        await Runner().RunAsync("component-store");

        Assert.DoesNotContain(_commands.Calls.Single().Arguments, a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reports_failures_and_reboot_requests_from_the_exit_code()
    {
        _commands.ExitCode = 87;
        var failed = await Runner().RunAsync("component-store");
        _commands.ExitCode = 3010;
        var reboot = await Runner().RunAsync("component-store");

        Assert.False(failed.Success);
        Assert.Contains("87", failed.Message);
        Assert.True(reboot.Success);
        Assert.True(reboot.RebootRequired);
    }

    [Fact]
    public async Task Update_cache_is_cleared_with_services_paused_and_restored()
    {
        _dir.WriteFile("SoftwareDistribution/Download/abc/update.cab", 5000);
        _dir.WriteFile("SoftwareDistribution/Download/def.psf", 3000);
        _services.Running.UnionWith(["wuauserv", "bits"]);

        var result = await Runner().RunAsync("windows-update-cache");

        Assert.True(result.Success);
        Assert.True(Directory.Exists(_dir.PathOf("SoftwareDistribution/Download")));
        Assert.Empty(Directory.EnumerateFiles(_dir.PathOf("SoftwareDistribution/Download"), "*", SearchOption.AllDirectories));
        Assert.Equal(["stop wuauserv", "stop bits", "start wuauserv", "start bits"], _services.Calls);
        Assert.Contains("已删除 2 个文件", result.Message);
    }

    [Fact]
    public async Task Services_that_were_stopped_stay_stopped()
    {
        _dir.CreateDirectory("SoftwareDistribution/Download");
        _services.Running.Add("wuauserv");

        await Runner().RunAsync("windows-update-cache");

        Assert.Equal(["stop wuauserv", "start wuauserv"], _services.Calls);
    }

    [Fact]
    public async Task Services_are_restarted_even_if_cleaning_fails()
    {
        _services.Running.Add("wuauserv");
        _services.FailOnStop = "bits";
        _services.Running.Add("bits");

        var result = await Runner().RunAsync("windows-update-cache");

        Assert.False(result.Success);
        Assert.Contains("start wuauserv", _services.Calls);
    }

    [Fact]
    public async Task Every_step_is_logged()
    {
        await Runner().RunAsync("hibernation-off");

        Assert.Equal(["run-start", "command", "command-done", "run-end"], _log.Entries.Select(e => e.Event));
        Assert.All(_log.Entries, e => Assert.Equal("hibernation-off", e.Rule));
    }

    [Theory]
    [InlineData(new[] { "system", "run", "x" }, "system run x")]
    [InlineData(new[] { @"C:\Program Files\DiskSlim\a b.json" }, "\"C:\\Program Files\\DiskSlim\\a b.json\"")]
    [InlineData(new[] { @"C:\dir with space\" }, "\"C:\\dir with space\\\\\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    [InlineData(new[] { "" }, "\"\"")]
    public void Helper_arguments_are_quoted_for_the_windows_command_line(string[] arguments, string expected)
    {
        Assert.Equal(expected, ElevatedHelper.QuoteArguments(arguments));
    }

    [Fact]
    public void Helper_results_can_only_be_written_into_the_helper_directory()
    {
        Assert.True(ElevatedHelper.IsValidResultPath(Path.Combine(ElevatedHelper.HelperDirectory, "result-1.json")));
        Assert.False(ElevatedHelper.IsValidResultPath(Path.Combine(ElevatedHelper.HelperDirectory, "sub", "result.json")));
        Assert.False(ElevatedHelper.IsValidResultPath(Path.Combine(ElevatedHelper.HelperDirectory, "result.exe")));
        Assert.False(ElevatedHelper.IsValidResultPath(@"C:\Windows\System32\result.json"));
        Assert.False(ElevatedHelper.IsValidResultPath(Path.Combine(ElevatedHelper.HelperDirectory, "..", "result.json")));
        Assert.Throws<ArgumentException>(() => ElevatedHelper.WriteResult(@"C:\Windows\x.json", new SystemTaskResult { TaskId = "x", Success = true }));
    }

    [Fact]
    public async Task Helper_refuses_unknown_tasks_before_starting_anything()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ElevatedHelper.RunAsync("diskslim.exe", "rm -rf", "log.jsonl"));
    }

    [Fact]
    public void Results_round_trip_through_json()
    {
        var result = new SystemTaskResult { TaskId = "component-store", Success = true, Message = "完成。", ExitCode = 0, FreeBytesBefore = 1, FreeBytesAfter = 5 };

        var json = System.Text.Json.JsonSerializer.Serialize(result, Core.Serialization.DiskSlimJsonContext.Default.SystemTaskResult);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Core.Serialization.DiskSlimJsonContext.Default.SystemTaskResult);

        Assert.Equal(result, back);
    }

    private sealed class FakeCommands : ICommandRunner
    {
        public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public int ExitCode { get; set; }

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(new CommandResult(ExitCode, "操作成功完成。"));
        }
    }

    private sealed class FakeServices : IServiceControl
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public string? FailOnStop { get; set; }

        public bool IsRunning(string serviceName) => Running.Contains(serviceName);

        public void Stop(string serviceName, TimeSpan timeout)
        {
            Calls.Add("stop " + serviceName);
            if (serviceName == FailOnStop)
            {
                throw new InvalidOperationException("无法停止服务");
            }

            Running.Remove(serviceName);
        }

        public void Start(string serviceName, TimeSpan timeout)
        {
            Calls.Add("start " + serviceName);
            Running.Add(serviceName);
        }
    }

    private sealed class MemoryLog : IOperationLog
    {
        public List<LogEntry> Entries { get; } = [];

        public string? FilePath => null;

        public void Write(LogEntry entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }
}
