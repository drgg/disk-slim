namespace DiskSlim.Core.Platform;

public static class DiskSpace
{
    private const long LowSpaceCap = 20L << 30;

    /// <summary>
    /// 剩余空间是否紧张：少于容量的 10%（大硬盘最多按 20 GB 算）。
    /// 这时“移入隔离区”“移到回收站”这类暂时还占着空间的清理方式要特别提醒。
    /// </summary>
    public static bool IsLow(long freeBytes, long totalBytes) => totalBytes > 0 && freeBytes < Math.Min(totalBytes / 10, LowSpaceCap);

    /// <summary>磁盘的剩余空间和总容量；读不到时返回 (0, 0)。</summary>
    public static (long Free, long Total) Query(string driveRoot)
    {
        try
        {
            var drive = new DriveInfo(driveRoot);
            return (drive.TotalFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }
}
