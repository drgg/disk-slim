using System.ComponentModel;

namespace DiskSlim.Core.Scanning;

public sealed record ScanOptions
{
    /// <summary>早于这个时间修改（或创建）的文件计为“可释放”，见 <see cref="Rules.FileAge"/>；为 null 时所有文件都计入。</summary>
    public DateTimeOffset? ReclaimableBefore { get; init; }

    /// <summary>
    /// 为 true 时，只读和系统文件不计为“可释放”——清理时它们会被保留，这样统计结果和实际清理一致。
    /// </summary>
    public bool ProtectedFilesAreNotReclaimable { get; init; }

    /// <summary>记录最大的多少个文件；0 表示不记录。</summary>
    public int LargestFileCount { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>最多保留多少条无法访问的目录明细（计数不受影响）。</summary>
    public int MaxRecordedErrors { get; init; } = 200;
}

public sealed record FileEntry(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc);

public sealed record ScanError(string Path, int ErrorCode)
{
    public string Message => new Win32Exception(ErrorCode).Message;
}

public sealed record ScanStats
{
    public long Files { get; init; }

    public long Directories { get; init; }

    public long Bytes { get; init; }

    /// <summary>无法（完整）读取的目录数，通常是权限不足。</summary>
    public long InaccessibleDirectories { get; init; }

    /// <summary>跳过的目录联接、符号链接和挂载点，避免重复计算。</summary>
    public long SkippedLinks { get; init; }

    /// <summary>OneDrive 等云盘中“仅在线”的文件，不占本地空间，未计入。</summary>
    public long CloudOnlyFiles { get; init; }

    /// <summary>尚未同步到本地的云盘目录，没有进入读取，避免触发下载。</summary>
    public long CloudOnlyDirectories { get; init; }
}

public sealed class ScanResult
{
    internal ScanResult(DirNode root, IReadOnlyList<FileEntry> largestFiles, ScanStats stats, IReadOnlyList<ScanError> errors, TimeSpan elapsed)
    {
        Root = root;
        LargestFiles = largestFiles;
        Stats = stats;
        Errors = errors;
        Elapsed = elapsed;
    }

    public DirNode Root { get; }

    /// <summary>按大小从大到小排列。</summary>
    public IReadOnlyList<FileEntry> LargestFiles { get; }

    public ScanStats Stats { get; }

    public IReadOnlyList<ScanError> Errors { get; }

    public TimeSpan Elapsed { get; }
}
