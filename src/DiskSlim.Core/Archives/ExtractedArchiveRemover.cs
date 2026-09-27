using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Core.Archives;

public sealed record ArchiveRemovalResult
{
    /// <summary>移入隔离区的文件数（一个分卷算一个）。</summary>
    public int Files { get; init; }

    public long Bytes { get; init; }

    public IReadOnlyList<QuarantineProblem> Skipped { get; init; } = [];

    /// <summary>这次移入隔离区的批次（每块盘一批）。</summary>
    public IReadOnlyList<QuarantineBatch> Batches { get; init; } = [];
}

/// <summary>移入了文件的一批隔离区：在哪块盘上、批次 id 是什么。</summary>
public sealed record QuarantineBatch(string DriveRoot, string SessionId);

/// <summary>
/// 把已经解压过的压缩包移入隔离区（可以恢复）。只有解压出来的文件夹仍然存在、压缩包在查找之后没有被改过时才移动；
/// 分卷压缩包的所有分卷一起移动。
/// </summary>
/// <param name="storeFor">每块盘的隔离区（移入必须在同一块盘上）。</param>
public sealed class ExtractedArchiveRemover(Func<string, QuarantineStore> storeFor, SafetyGuard guard, IOperationLog? log = null, TimeProvider? time = null)
{
    private const string RuleId = "extracted-archives";

    private readonly IOperationLog _log = log ?? NullOperationLog.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <param name="dryRun">为 true 时只检查，不移动任何文件。</param>
    public ArchiveRemovalResult Remove(IReadOnlyList<ExtractedArchive> archives, bool dryRun, CancellationToken cancellationToken = default)
    {
        var skipped = new List<QuarantineProblem>();
        var writers = new Dictionary<string, QuarantineWriter>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        long bytes = 0;

        if (!dryRun)
        {
            _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "run-start", Detail = $"移除已经解压过的压缩包：{archives.Count} 个" });
        }

        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Problem(archive) is { } problem)
            {
                skipped.Add(new QuarantineProblem(archive.Path, problem));
                continue;
            }

            foreach (var part in archive.Parts)
            {
                if (!dryRun)
                {
                    var drive = Path.GetPathRoot(part.Path)!;
                    if (!writers.TryGetValue(drive, out var writer))
                    {
                        writers[drive] = writer = storeFor(drive).BeginSession($"已经解压过的压缩包（{archives.Count} 个）", _time.GetLocalNow());
                    }

                    try
                    {
                        writer.Move(part.Path, part.Bytes, RuleId);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(new QuarantineProblem(part.Path, ex.Message));
                        _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "failed", Rule = RuleId, Path = part.Path, Bytes = part.Bytes, Detail = ex.Message });
                        continue;
                    }

                    _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "quarantined", Rule = RuleId, Path = part.Path, Bytes = part.Bytes, Detail = "已解压到：" + archive.ExtractedTo });
                }

                files++;
                bytes += part.Bytes;
            }
        }

        if (!dryRun)
        {
            _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "run-end", Bytes = bytes, Detail = $"移入隔离区 {files} 个文件，跳过 {skipped.Count} 个" });
        }

        return new ArchiveRemovalResult
        {
            Files = files,
            Bytes = bytes,
            Skipped = skipped,
            Batches = writers.Where(w => w.Value.Count > 0).Select(w => new QuarantineBatch(w.Key, w.Value.Id)).ToList(),
        };
    }

    /// <summary>不能移动的原因；可以移动时返回 null。</summary>
    private string? Problem(ExtractedArchive archive)
    {
        if (!Directory.Exists(archive.ExtractedTo))
        {
            return "解压出来的文件夹已经不在了，为安全起见不移动压缩包";
        }

        foreach (var part in archive.Parts)
        {
            if (guard.Check(part.Path) is { } reason)
            {
                return "位于受保护的位置：" + reason;
            }

            var info = new FileInfo(part.Path);
            if (!info.Exists || info.Length != part.Bytes || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != part.LastWriteTimeUtc)
            {
                return "查找之后被修改或删除过，没有移动";
            }
        }

        return null;
    }
}
