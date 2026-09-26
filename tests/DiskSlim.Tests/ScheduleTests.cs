using System.Xml.Linq;
using DiskSlim.Cli;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scheduling;
using DiskSlim.Core.SystemTasks;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Tests;

/// <summary>
/// 定时清理。任务计划程序用假的 schtasks 代替，测试不会注册任何真实的任务；清理只发生在临时目录里。
/// 运行清理任务的测试都放在这个类里：它们共用一个系统级的互斥锁，不能并行。
/// </summary>
public sealed class ScheduleTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111-2222-3333-1001";
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly FakeSchtasks _schtasks = new();

    public void Dispose() => _dir.Dispose();

    private ScheduleManager Manager() => new(_schtasks, _dir.PathOf("schedule"), Sid);

    private string FakeProgram() => _dir.WriteFile("app/DiskSlimApp.exe", 10);

    // ───── 计划时间 ─────

    [Theory]
    [InlineData("2026-09-25 10:00", "2026-09-27 12:00")] // 周五 → 周日
    [InlineData("2026-09-27 11:00", "2026-09-27 12:00")] // 周日上午 → 当天
    [InlineData("2026-09-27 12:00", "2026-10-04 12:00")] // 正好在点上 → 下周
    public void Weekly_next_run_is_the_coming_day_and_time(string now, string expected)
    {
        var settings = new ScheduleSettings { Program = "x", Day = DayOfWeek.Sunday, Time = new TimeOnly(12, 0) };

        Assert.Equal(DateTime.Parse(expected), settings.NextRun(DateTime.Parse(now)));
        Assert.Equal("每周日 12:00", settings.Describe());
    }

    [Fact]
    public void Daily_next_run_is_today_or_tomorrow()
    {
        var settings = new ScheduleSettings { Program = "x", Frequency = ScheduleFrequency.Daily, Time = new TimeOnly(8, 30) };

        Assert.Equal(new DateTime(2026, 9, 25, 8, 30, 0), settings.NextRun(new DateTime(2026, 9, 25, 8, 0, 0)));
        Assert.Equal(new DateTime(2026, 9, 26, 8, 30, 0), settings.NextRun(new DateTime(2026, 9, 25, 9, 0, 0)));
        Assert.Equal("每天 08:30", settings.Describe());
    }

    // ───── 任务定义 ─────

    [Fact]
    public void Task_definition_runs_as_the_user_without_elevation_and_escapes_the_path()
    {
        var settings = new ScheduleSettings { Program = @"C:\A & B\DiskSlimApp.exe", Day = DayOfWeek.Sunday };

        var xml = ScheduledTaskDefinition.Build(settings, Sid, new DateTime(2026, 9, 25, 10, 0, 0));
        var task = XDocument.Parse(xml[xml.IndexOf("<Task", StringComparison.Ordinal)..]).Root!;
        XNamespace ns = ScheduledTaskDefinition.Ns;

        var trigger = task.Element(ns + "Triggers")!.Element(ns + "CalendarTrigger")!;
        Assert.Equal("2026-09-27T12:00:00", trigger.Element(ns + "StartBoundary")!.Value);
        Assert.NotNull(trigger.Element(ns + "ScheduleByWeek")!.Element(ns + "DaysOfWeek")!.Element(ns + "Sunday"));
        var principal = task.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal(Sid, principal.Element(ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(ns + "RunLevel")!.Value);
        var exec = task.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal("\"C:\\A & B\\DiskSlimApp.exe\"", exec.Element(ns + "Command")!.Value);
        Assert.Equal(ScheduleManager.RunArgument, exec.Element(ns + "Arguments")!.Value);
        Assert.Contains("&amp;", xml);
        Assert.Equal("true", task.Element(ns + "Settings")!.Element(ns + "DisallowStartIfOnBatteries")!.Value);
    }

    [Fact]
    public void Task_enabled_state_is_read_from_the_settings_not_the_trigger()
    {
        var xml = ScheduledTaskDefinition.Build(new ScheduleSettings { Program = @"C:\x.exe" }, Sid, DateTime.Now);
        var disabled = xml.Replace("<Enabled>true</Enabled>\r\n    <Hidden>", "<Enabled>false</Enabled>\r\n    <Hidden>").Replace("<Enabled>true</Enabled>\n    <Hidden>", "<Enabled>false</Enabled>\n    <Hidden>");

        Assert.True(ScheduledTaskDefinition.IsEnabled(xml));
        Assert.False(ScheduledTaskDefinition.IsEnabled(disabled));
        Assert.Null(ScheduledTaskDefinition.IsEnabled("错误: 系统找不到指定的文件。"));
    }

    // ───── 开启和关闭 ─────

    [Fact]
    public async Task Enabling_registers_the_task_then_saves_settings_and_disabling_removes_both()
    {
        var manager = Manager();
        var settings = new ScheduleSettings { Program = FakeProgram(), Frequency = ScheduleFrequency.Daily, Time = new TimeOnly(20, 30) };

        Assert.Null(await manager.EnableAsync(settings, DateTimeOffset.Now));

        Assert.Contains("<DaysInterval>1</DaysInterval>", _schtasks.Registered);
        var create = _schtasks.Calls.Single(c => c[0] == "/Create");
        Assert.Equal(["/Create", "/TN", manager.TaskName, "/XML", Path.Join(manager.Directory, "task.xml"), "/F"], create);
        Assert.Equal(System.Text.Encoding.Unicode.Preamble.ToArray(), File.ReadAllBytes(Path.Join(manager.Directory, "task.xml"))[..2]);
        Assert.Equal(ScheduleFrequency.Daily, manager.LoadSettings()!.Frequency);
        Assert.True((await manager.GetStatusAsync()).IsActive);

        Assert.Null(await manager.DisableAsync());
        Assert.Null(_schtasks.Registered);
        Assert.False(manager.IsEnabled);
        Assert.False((await manager.GetStatusAsync()).TaskRegistered);
    }

    [Fact]
    public async Task Failed_registration_saves_nothing()
    {
        _schtasks.CreateExitCode = 1;
        var manager = Manager();

        var error = await manager.EnableAsync(new ScheduleSettings { Program = FakeProgram() }, DateTimeOffset.Now);

        Assert.Contains("注册定时任务失败", error);
        Assert.False(manager.IsEnabled);
        Assert.Contains("找不到", await manager.EnableAsync(new ScheduleSettings { Program = _dir.PathOf("missing.exe") }, DateTimeOffset.Now));
    }

    [Fact]
    public async Task Disabling_turns_off_cleaning_even_if_the_task_cannot_be_deleted()
    {
        var manager = Manager();
        await manager.EnableAsync(new ScheduleSettings { Program = FakeProgram() }, DateTimeOffset.Now);
        _schtasks.DeleteExitCode = 5;

        var error = await manager.DisableAsync();

        Assert.Contains("不会再清理任何东西", error);
        Assert.False(manager.IsEnabled); // 定时运行时检查这个，所以什么也不会做
    }

    [Fact]
    public async Task Status_explains_what_went_wrong()
    {
        var manager = Manager();
        var program = FakeProgram();
        await manager.EnableAsync(new ScheduleSettings { Program = program }, DateTimeOffset.Now);

        _schtasks.Registered = _schtasks.Registered!.Replace("<Enabled>true</Enabled>\r\n    <Hidden>", "<Enabled>false</Enabled>\r\n    <Hidden>");
        Assert.Contains("被禁用", (await manager.GetStatusAsync()).Problem);

        File.Delete(program);
        _schtasks.Registered = "<Task />";
        Assert.Contains("程序的位置变了", (await manager.GetStatusAsync()).Problem);

        _schtasks.Registered = null;
        Assert.Contains("不见了", (await manager.GetStatusAsync()).Problem);
    }

    [Fact]
    public void Run_records_are_listed_newest_first()
    {
        var manager = Manager();
        manager.AppendRun(new ScheduledRunRecord { Time = DateTimeOffset.Now.AddDays(-7), Success = true, Message = "第一次" });
        File.AppendAllText(Path.Join(manager.Directory, "runs.jsonl"), "{半行\n");
        manager.AppendRun(new ScheduledRunRecord { Time = DateTimeOffset.Now, Success = false, Message = "第二次" });

        Assert.Equal(["第二次", "第一次"], manager.RecentRuns().Select(r => r.Message));
        Assert.Single(manager.RecentRuns(1));
    }

    // ───── 清理任务 ─────

    private RuleSet TestRules() => new()
    {
        Rules =
        [
            new CleanupRule { Id = "chat", Name = "聊天记录", Risk = RiskLevel.High, Paths = [_dir.PathOf("chat")] },
            new CleanupRule { Id = "cache", Name = "缓存", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = [_dir.PathOf("cache")], MinAgeDays = 1 },
            new CleanupRule { Id = "browser", Name = "浏览器缓存", Risk = RiskLevel.Low, Action = CleanupAction.Delete, Paths = [_dir.PathOf("browser")], Processes = ["fakebrowser"] },
            new CleanupRule { Id = "models", Name = "模型", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine, Paths = [_dir.PathOf("models")] },
        ],
    };

    private ScheduledCleanupJob Job(ScheduleManager manager) => new(
        TestRules(),
        Path.GetPathRoot(_dir.Root)!,
        manager,
        history: null,
        logDirectory: _dir.PathOf("logs"),
        quarantine: new QuarantineStore(_dir.PathOf("quarantine")),
        runningProcesses: new HashSet<string>(["fakebrowser"], StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Scheduled_job_only_cleans_safe_items_and_records_the_run()
    {
        _dir.WriteFile("cache/old.tmp", 4096, Old);
        _dir.WriteFile("cache/new.tmp", 100);
        _dir.WriteFile("browser/data", 2048, Old);
        _dir.WriteFile("models/big.bin", 8192, Old);
        _dir.WriteFile("chat/history.db", 100, Old);
        var manager = Manager();

        var record = Job(manager).Run();

        Assert.True(record.Success);
        Assert.False(File.Exists(_dir.PathOf("cache/old.tmp")));
        Assert.True(File.Exists(_dir.PathOf("cache/new.tmp")));       // 太新
        Assert.True(File.Exists(_dir.PathOf("browser/data")));        // 程序正在运行
        Assert.True(File.Exists(_dir.PathOf("models/big.bin")));      // “需要确认”的项目从不自动清理
        Assert.True(File.Exists(_dir.PathOf("chat/history.db")));     // 仅供参考
        Assert.Equal(1, record.Files);
        Assert.Equal(4096, record.CleanedBytes);
        Assert.Equal(["浏览器缓存"], record.Skipped);
        Assert.Contains("相关程序正在运行", record.Message);
        Assert.True(File.Exists(record.LogPath));
        Assert.StartsWith("scheduled-", Path.GetFileName(record.LogPath));
        Assert.Equal(record.Time, Assert.Single(manager.RecentRuns()).Time);
        Assert.Equal(["cache", "browser"], ScheduledCleanupJob.AutomaticRules(TestRules()).Select(r => r.Id));
    }

    [Fact]
    public void Scheduled_job_does_nothing_while_another_run_holds_the_lock()
    {
        _dir.WriteFile("cache/old.tmp", 4096, Old);
        var manager = Manager();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, ScheduledCleanupJob.MutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            var record = Job(manager).Run();

            Assert.False(record.Success);
            Assert.Contains("另一次定时清理正在运行", record.Message);
            Assert.True(File.Exists(_dir.PathOf("cache/old.tmp")));
            Assert.Empty(manager.RecentRuns());
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    // ───── 命令行 ─────

    private (int ExitCode, string Output) RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run([.. args, "--no-color"], stdout, stderr, new StringReader(""), interactive: false,
            history: new Core.History.SnapshotStore(_dir.PathOf("history")), schedule: Manager());
        return (exitCode, stdout.ToString() + stderr);
    }

    [Fact]
    public void Command_line_enables_shows_and_disables_the_schedule()
    {
        var (refused, refusedOutput) = RunCli("schedule", "enable");
        Assert.Equal(CliApp.ExitOk, refused);
        Assert.Contains("需要确认才能开启", refusedOutput);
        Assert.Null(_schtasks.Registered);

        var (exitCode, output) = RunCli("schedule", "enable", "--weekly", "周六", "--at", "20:30", "--yes");
        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("已开启定时清理：每周六 20:30", output);
        Assert.Contains("<Saturday />", _schtasks.Registered);

        var status = RunCli("schedule").Output;
        Assert.Contains("已开启 · 每周六 20:30", status);
        Assert.Contains("会自动清理的项目", status);

        Assert.Contains("已关闭定时清理", RunCli("schedule", "disable").Output);
        Assert.Null(_schtasks.Registered);
        Assert.Contains("未开启", RunCli("schedule").Output);
    }

    [Theory]
    [InlineData("schedule", "enable", "--execute")]
    [InlineData("schedule", "--daily")]
    [InlineData("schedule", "run", "--at", "12:00")]
    [InlineData("schedule", "enable", "--weekly", "funday")]
    [InlineData("schedule", "enable", "--at", "25:00")]
    [InlineData("schedule", "--rules", "x.json")]
    [InlineData("schedule", "pause")]
    [InlineData("clean", "--daily")]
    [InlineData("schedule", "run", "--yes")]
    public void Schedule_options_are_checked(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Theory]
    [InlineData("sun", DayOfWeek.Sunday)]
    [InlineData("Monday", DayOfWeek.Monday)]
    [InlineData("周三", DayOfWeek.Wednesday)]
    [InlineData("星期天", DayOfWeek.Sunday)]
    [InlineData("六", DayOfWeek.Saturday)]
    [InlineData("7", DayOfWeek.Sunday)]
    [InlineData("1", DayOfWeek.Monday)]
    public void Days_can_be_written_in_english_chinese_or_numbers(string text, DayOfWeek expected)
    {
        Assert.Equal(expected, CliParser.ParseDay(text));
    }

    [Fact]
    public void Schedule_options_parse()
    {
        var options = CliParser.Parse(["schedule", "enable", "--daily", "--at", "8:05", "-y"]).Options!;

        Assert.Equal((ScheduleVerb.Enable, ScheduleFrequency.Daily, new TimeOnly(8, 5), true), (options.ScheduleVerb, options.Frequency, options.At, options.AssumeYes));
        Assert.Equal(ScheduleVerb.Run, CliParser.Parse(["schedule", "run", "--execute", "--yes"]).Options!.ScheduleVerb);
    }

    // ───── 图形界面 ─────

    [Fact]
    public async Task Schedule_page_asks_before_enabling_and_shows_the_state()
    {
        var page = new ScheduleViewModel(TestRules(), Manager(), FakeProgram());
        await page.RefreshAsync();
        Assert.Equal("未开启", page.StatusText);
        Assert.Equal("开启定时清理…", page.EnableButtonText);
        Assert.Equal(["缓存（1 天前的文件）", "浏览器缓存"], page.AutomaticRules);

        string? asked = null;
        page.Confirm = question => { asked = question; return false; };
        page.SelectedTime = page.Times.Single(t => t.Label == "21:30");
        await page.EnableAsync();
        Assert.Contains("浏览器缓存", asked);
        Assert.Null(_schtasks.Registered); // 用户点了“否”

        page.Confirm = _ => true;
        await page.EnableAsync();
        Assert.StartsWith("已开启 · 每周日 21:30", page.StatusText);
        Assert.Equal("保存设置", page.EnableButtonText);
        Assert.True(page.IsEnabled);

        await page.DisableAsync();
        Assert.Equal("未开启", page.StatusText);
        Assert.Null(_schtasks.Registered);
    }

    /// <summary>假的 schtasks：在内存里记一个任务，不碰真实的任务计划程序。</summary>
    private sealed class FakeSchtasks : ICommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        /// <summary>已注册任务的 XML；为 null 表示没有注册。</summary>
        public string? Registered { get; set; }

        public int CreateExitCode { get; set; }

        public int DeleteExitCode { get; set; }

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
        {
            Assert.Equal(SystemTaskRunner.SystemProgram("schtasks.exe"), fileName);
            Calls.Add(arguments);
            var result = arguments[0] switch
            {
                "/Query" => Registered is null ? new CommandResult(1, "错误: 系统找不到指定的文件。") : new CommandResult(0, Registered),
                "/Create" when CreateExitCode == 0 => Create(arguments[4]),
                "/Create" => new CommandResult(CreateExitCode, "错误: 拒绝访问。"),
                "/Delete" when DeleteExitCode == 0 => Delete(),
                "/Delete" => new CommandResult(DeleteExitCode, "错误: 拒绝访问。"),
                _ => throw new InvalidOperationException("不应该调用：" + string.Join(" ", arguments)),
            };
            return Task.FromResult(result);
        }

        private CommandResult Create(string xmlFile)
        {
            Registered = File.ReadAllText(xmlFile);
            return new CommandResult(0, "成功: 成功创建计划任务。");
        }

        private CommandResult Delete()
        {
            Registered = null;
            return new CommandResult(0, "成功: 计划的任务被成功删除。");
        }
    }
}
