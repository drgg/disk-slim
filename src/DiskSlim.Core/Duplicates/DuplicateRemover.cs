using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Core.Duplicates;

/// <summary>用户决定移除的重复文件：<see cref="Remove"/> 中的文件移入隔离区，其余的保留。</summary>
public sealed record DuplicateSelection(DuplicateGroup Group, IReadOnlyList<DuplicateFile> Remove);

public sealed record DuplicateRemovalResult
{
    public int Files { get; init; }

    public long Bytes { get; init; }

    public IReadOnlyList<QuarantineProblem> Skipped { get; init; } = [];

    /// <summary>这次移入隔离区的批次；没有移入任何文件时为 null。</summary>
    public string? QuarantineSessionId { get; init; }
}

/// <summary>
/// 把选中的重复文件移入隔离区（可以恢复）。每组至少保留一份，而且保留的那份必须仍然存在、在扫描后没有被修改过。
/// </summary>
public sealed class DuplicateRemover(QuarantineStore store, SafetyGuard guard, IOperationLog? log = null, TimeProvider? time = null)
{
    private const string RuleId = "duplicates";

    private readonly IOperationLog _log = log ?? NullOperationLog.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <param name="dryRun">为 true 时只检查，不移动任何文件。</param>
    public DuplicateRemovalResult Remove(IReadOnlyList<DuplicateSelection> selections, bool dryRun, CancellationToken cancellationToken = default)
    {
        var skipped = new List<QuarantineProblem>();
        QuarantineWriter? writer = null;
        int files = 0;
        long bytes = 0;

        if (!dryRun)
        {
            _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "run-start", Detail = $"移除重复文件：{selections.Count} 组" });
        }

        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = selection.Group;
            var remove = selection.Remove.Where(f => group.Files.Contains(f)).Distinct().ToList();
            if (remove.Count == 0)
            {
                continue;
            }

            // 每组至少保留一份，而且要确认它还在、内容没变（大小和修改时间与扫描时一致）。
            var keepers = group.Files.Except(remove).ToList();
            if (keepers.Count == 0)
            {
                skipped.AddRange(remove.Select(f => new QuarantineProblem(f.Path, "这一组的文件全被选中了，至少要保留一份")));
                continue;
            }

            if (!keepers.Any(IsUnchanged))
            {
                skipped.AddRange(remove.Select(f => new QuarantineProblem(f.Path, "要保留的那份已经不在或被修改过，为安全起见这一组都不动")));
                continue;
            }

            foreach (var file in remove)
            {
                if (file.IsProtected)
                {
                    skipped.Add(new QuarantineProblem(file.Path, file.ProtectedReason!));
                    continue;
                }

                if (guard.Check(file.Path) is { } reason)
                {
                    skipped.Add(new QuarantineProblem(file.Path, "位于受保护的位置：" + reason));
                    continue;
                }

                if (!IsUnchanged(file))
                {
                    skipped.Add(new QuarantineProblem(file.Path, "扫描后被修改或删除过，没有移动"));
                    continue;
                }

                if (!dryRun)
                {
                    writer ??= store.BeginSession($"重复文件（{selections.Count} 组）", _time.GetLocalNow());
                    try
                    {
                        writer.Move(file.Path, file.Bytes, RuleId);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(new QuarantineProblem(file.Path, ex.Message));
                        _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "failed", Rule = RuleId, Path = file.Path, Bytes = file.Bytes, Detail = ex.Message });
                        continue;
                    }

                    _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "quarantined", Rule = RuleId, Path = file.Path, Bytes = file.Bytes, Detail = "保留：" + keepers[0].Path });
                }

                files++;
                bytes += file.Bytes;
            }
        }

        if (!dryRun)
        {
            _log.Write(new LogEntry { Time = _time.GetLocalNow(), Event = "run-end", Bytes = bytes, Detail = $"移入隔离区 {files} 个文件，跳过 {skipped.Count} 个" });
        }

        return new DuplicateRemovalResult
        {
            Files = files,
            Bytes = bytes,
            Skipped = skipped,
            QuarantineSessionId = writer is { Count: > 0 } ? writer.Id : null,
        };
    }

    /// <summary>文件还在，大小和修改时间与扫描时一致。</summary>
    private static bool IsUnchanged(DuplicateFile file)
    {
        var info = new FileInfo(file.Path);
        return info.Exists && info.Length == file.Bytes
            && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) == file.LastWriteTimeUtc;
    }
}
