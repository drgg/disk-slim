namespace DiskSlim.Core.Rules;

/// <summary>
/// 判断文件“有多旧”。取最后修改时间和创建时间中较晚的那个：
/// 解压出来的文件会保留压缩包里的旧修改时间，但创建时间是刚才，这类文件可能正被安装程序使用，不能当成旧文件。
/// </summary>
public static class FileAge
{
    public static DateTimeOffset? Cutoff(int? minAgeDays, TimeProvider time) =>
        minAgeDays is > 0 and var days ? time.GetUtcNow() - TimeSpan.FromDays(days) : null;

    public static DateTimeOffset EffectiveTimeUtc(DateTimeOffset lastWriteUtc, DateTimeOffset creationUtc) =>
        lastWriteUtc > creationUtc ? lastWriteUtc : creationUtc;

    public static bool IsOlderThan(DateTimeOffset lastWriteUtc, DateTimeOffset creationUtc, DateTimeOffset? cutoff) =>
        cutoff is not { } c || EffectiveTimeUtc(lastWriteUtc, creationUtc) < c;

    public static bool IsOlderThan(FileSystemInfo info, DateTimeOffset? cutoff) =>
        IsOlderThan(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero), cutoff);
}
