using System.Globalization;
using System.Text.Json.Serialization;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Scheduling;

[JsonConverter(typeof(KebabCaseEnumConverter<ScheduleFrequency>))]
public enum ScheduleFrequency
{
    Weekly,
    Daily,
}

/// <summary>定时清理的设置（schedule\settings.json）。这个文件存在，才表示用户开启了定时清理。</summary>
public sealed record ScheduleSettings
{
    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Weekly;

    /// <summary>每周哪一天（<see cref="Frequency"/> 为每周时）。</summary>
    [JsonConverter(typeof(KebabCaseEnumConverter<DayOfWeek>))]
    public DayOfWeek Day { get; init; } = DayOfWeek.Sunday;

    public TimeOnly Time { get; init; } = new(12, 0);

    /// <summary>定时任务启动的程序（DiskSlimApp.exe 的完整路径）。</summary>
    public required string Program { get; init; }

    public DateTimeOffset EnabledAt { get; init; }

    public static string DayName(DayOfWeek day) => "周" + "日一二三四五六"[(int)day];

    /// <summary>例如 “每周日 12:00”“每天 08:30”。</summary>
    public string Describe() => (Frequency == ScheduleFrequency.Daily ? "每天" : "每" + DayName(Day)) + " " + Time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary><paramref name="now"/> 之后第一次按计划运行的时间（本地时间）。</summary>
    public DateTime NextRun(DateTime now)
    {
        var candidate = now.Date + Time.ToTimeSpan();
        if (Frequency == ScheduleFrequency.Daily)
        {
            return candidate > now ? candidate : candidate.AddDays(1);
        }

        var days = ((int)Day - (int)now.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(days);
        return candidate > now ? candidate : candidate.AddDays(7);
    }
}

/// <summary>一次定时清理的结果（schedule\runs.jsonl，一行一次）。</summary>
public sealed record ScheduledRunRecord
{
    public required DateTimeOffset Time { get; init; }

    public required bool Success { get; init; }

    public string Message { get; init => field = value ?? ""; } = "";

    /// <summary>磁盘剩余空间的变化（清理前后的差值）。</summary>
    public long FreedBytes { get; init; }

    public long CleanedBytes { get; init; }

    public long Files { get; init; }

    /// <summary>因为相关程序正在运行而跳过的项目。</summary>
    public IReadOnlyList<string> Skipped { get; init => field = value ?? []; } = [];

    public long Failed { get; init; }

    public string? LogPath { get; init; }

    public bool SnapshotSaved { get; init; }

    public double ElapsedSeconds { get; init; }
}
