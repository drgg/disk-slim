using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Serialization;
using DiskSlim.Core.SystemTasks;

namespace DiskSlim.Core.Scheduling;

/// <summary>定时清理现在的状态。</summary>
/// <param name="Settings">用户的设置；为 null 表示没有开启。</param>
/// <param name="TaskRegistered">任务计划程序里有没有这个任务。</param>
/// <param name="TaskEnabled">任务在任务计划程序里是否处于启用状态。</param>
public sealed record ScheduleStatus(ScheduleSettings? Settings, bool TaskRegistered, bool TaskEnabled, bool ProgramExists)
{
    public bool IsActive => Settings is not null && TaskRegistered && TaskEnabled && ProgramExists;

    /// <summary>需要用户处理的问题；一切正常或没有开启时为 null。</summary>
    public string? Problem => (Settings, TaskRegistered) switch
    {
        (null, true) => "任务计划程序里还有 DiskSlim 的定时任务，但设置已经丢失：请重新开启或关闭定时清理。",
        (null, false) => null,
        (_, false) => "任务计划程序里的定时任务不见了（可能被手动删除），请重新开启定时清理。",
        _ when !TaskEnabled => "定时任务在任务计划程序里被禁用了，不会运行。重新开启定时清理可以恢复。",
        _ when !ProgramExists => $"程序的位置变了（找不到 {Settings!.Program}），请在新位置重新开启定时清理。",
        _ => null,
    };
}

/// <summary>
/// 开启、关闭定时清理，读写它的设置和运行记录。任务通过系统自带的 schtasks.exe 注册到“任务计划程序”，
/// 名字带上当前用户的 SID，同一台电脑上的不同用户互不影响。
/// </summary>
/// <remarks>
/// 开启时先注册任务再保存设置；关闭时先删除设置再删除任务。定时运行时要求设置存在（见 <see cref="IsEnabled"/>），
/// 所以即使任务没能删掉，关闭后也不会再清理任何东西。
/// </remarks>
public sealed class ScheduleManager
{
    /// <summary>定时任务启动 DiskSlimApp.exe 时带的参数。</summary>
    public const string RunArgument = "--scheduled-run";

    internal const string SettingsFileName = "settings.json";
    internal const string RunsFileName = "runs.jsonl";
    internal const string TaskFileName = "task.xml";

    private static readonly JsonSerializerOptions LineOptions = new(DiskSlimJsonContext.Default.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions FileOptions = new(DiskSlimJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ICommandRunner _commands;

    /// <param name="commands">运行 schtasks.exe；测试时替换成假的实现。</param>
    /// <param name="directory">设置和运行记录的目录，默认 %LOCALAPPDATA%\DiskSlim\schedule。</param>
    /// <param name="userSid">任务以谁的身份运行，默认当前用户。</param>
    public ScheduleManager(ICommandRunner? commands = null, string? directory = null, string? userSid = null)
    {
        _commands = commands ?? new ProcessCommandRunner();
        Directory = Path.GetFullPath(directory ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSlim", "schedule"));
        UserSid = userSid ?? SystemInfo.CurrentUserSid();
    }

    public string Directory { get; }

    public string UserSid { get; }

    /// <summary>任务计划程序里的任务名。</summary>
    public string TaskName => $"DiskSlim Scheduled Cleanup-{UserSid}";

    private static string Schtasks => SystemTaskRunner.SystemProgram("schtasks.exe");

    /// <summary>用户是否开启了定时清理（设置文件存在且有效）。</summary>
    public bool IsEnabled => LoadSettings() is not null;

    public ScheduleSettings? LoadSettings()
    {
        try
        {
            var path = Path.Join(Directory, SettingsFileName);
            return File.Exists(path)
                ? (ScheduleSettings?)JsonSerializer.Deserialize(File.ReadAllText(path), FileOptions.GetTypeInfo(typeof(ScheduleSettings)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task<ScheduleStatus> GetStatusAsync()
    {
        var settings = LoadSettings();
        var query = await _commands.RunAsync(Schtasks, ["/Query", "/TN", TaskName, "/XML"]);
        var registered = query.ExitCode == 0;
        var enabled = registered && ScheduledTaskDefinition.IsEnabled(query.Output) != false;
        var programExists = settings is not null && File.Exists(settings.Program);
        return new ScheduleStatus(settings, registered, enabled, programExists);
    }

    /// <summary>注册（或更新）定时任务并保存设置。失败时返回原因，什么也不保存。</summary>
    public async Task<string?> EnableAsync(ScheduleSettings settings, DateTimeOffset now)
    {
        if (!File.Exists(settings.Program))
        {
            return $"找不到 {settings.Program}。";
        }

        System.IO.Directory.CreateDirectory(Directory);
        var xml = ScheduledTaskDefinition.Build(settings, UserSid, now.LocalDateTime);
        var taskFile = Path.Join(Directory, TaskFileName);
        File.WriteAllText(taskFile, xml, Encoding.Unicode); // schtasks 读取的任务定义是 UTF-16

        var result = await _commands.RunAsync(Schtasks, ["/Create", "/TN", TaskName, "/XML", taskFile, "/F"]);
        if (result.ExitCode != 0)
        {
            return $"注册定时任务失败（schtasks 返回 {result.ExitCode}）：{result.Output.Trim()}";
        }

        var saved = settings with { EnabledAt = now };
        WriteAtomically(Path.Join(Directory, SettingsFileName), JsonSerializer.Serialize(saved, FileOptions.GetTypeInfo(typeof(ScheduleSettings))));
        return null;
    }

    /// <summary>关闭定时清理：先删除设置（之后定时运行什么也不会做），再删除任务。</summary>
    public async Task<string?> DisableAsync()
    {
        var settingsFile = Path.Join(Directory, SettingsFileName);
        if (File.Exists(settingsFile))
        {
            File.Delete(settingsFile);
        }

        var query = await _commands.RunAsync(Schtasks, ["/Query", "/TN", TaskName]);
        if (query.ExitCode != 0)
        {
            return null; // 本来就没有
        }

        var result = await _commands.RunAsync(Schtasks, ["/Delete", "/TN", TaskName, "/F"]);
        return result.ExitCode == 0
            ? null
            : $"设置已经关闭，定时任务不会再清理任何东西；但没能从任务计划程序里删除它（schtasks 返回 {result.ExitCode}）：{result.Output.Trim()}";
    }

    /// <summary>最近的运行记录，从新到旧。</summary>
    public IReadOnlyList<ScheduledRunRecord> RecentRuns(int count = 10)
    {
        var path = Path.Join(Directory, RunsFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lines = new StreamReader(stream, Encoding.UTF8).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var records = new List<ScheduledRunRecord>();
        foreach (var line in lines.Reverse())
        {
            try
            {
                if (JsonSerializer.Deserialize(line, LineOptions.GetTypeInfo(typeof(ScheduledRunRecord))) is ScheduledRunRecord record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // 写到一半的行
            }

            if (records.Count >= count)
            {
                break;
            }
        }

        return records;
    }

    public void AppendRun(ScheduledRunRecord record)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var line = JsonSerializer.Serialize(record, LineOptions.GetTypeInfo(typeof(ScheduledRunRecord))) + "\n";
        File.AppendAllText(Path.Join(Directory, RunsFileName), line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>找到定时任务要启动的 DiskSlimApp.exe：和当前程序放在一起的，或者开发时 DiskSlim.Desktop 的输出目录里的。</summary>
    public static string? FindDesktopApp(string? baseDirectory = null)
    {
        var start = baseDirectory ?? AppContext.BaseDirectory;
        var sideBySide = Path.Combine(start, "DiskSlimApp.exe");
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var desktop = new[] { Path.Combine(dir.FullName, "DiskSlim.Desktop", "bin"), Path.Combine(dir.FullName, "src", "DiskSlim.Desktop", "bin") }
                .FirstOrDefault(System.IO.Directory.Exists);
            if (desktop is not null)
            {
                return System.IO.Directory.EnumerateFiles(desktop, "DiskSlimApp.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        return null;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
