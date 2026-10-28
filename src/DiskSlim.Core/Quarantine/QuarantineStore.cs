using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.Quarantine;

/// <summary>隔离区清单中的一条记录（manifest.jsonl，一行一条，只追加不修改）。</summary>
public sealed record QuarantineRecord
{
    /// <summary>added（准备移入）、failed（移入失败）、restored（已恢复）。</summary>
    public required string Event { get; init; }

    public required int Index { get; init; }

    public string? OriginalPath { get; init; }

    public string? StoredName { get; init; }

    public long? Bytes { get; init; }

    public string? Rule { get; init; }

    public DateTimeOffset Time { get; init; }
}

/// <summary>一批隔离的元数据（session.json）。</summary>
public sealed record QuarantineSessionInfo
{
    public required string Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Description { get; init => field = value ?? ""; } = "";
}

public sealed record QuarantineItem(int Index, string OriginalPath, string StoredPath, long Bytes, string? Rule)
{
    /// <summary>这一项是整个文件夹（只有旧版工具的批次里会有）。</summary>
    public bool IsDirectory { get; init; }
}

/// <summary>隔离区中的一批文件（一次清理产生一批）。</summary>
public sealed record QuarantineSession(string Id, string Directory, DateTimeOffset CreatedAt, string Description, IReadOnlyList<QuarantineItem> Items)
{
    public long TotalBytes => Items.Sum(i => i.Bytes);

    /// <summary>旧版（脚本版）工具留下的批次，位于磁盘根目录的 <c>.diskslim-quarantine</c> 里。</summary>
    public bool IsLegacy { get; init; }
}

public sealed record QuarantineProblem(string Path, string Reason);

public sealed record RestoreResult(int Restored, long RestoredBytes, IReadOnlyList<QuarantineProblem> Problems);

public sealed record PurgeResult(int Purged, long PurgedBytes, IReadOnlyList<QuarantineProblem> Problems);

/// <summary>
/// 隔离区：清理时文件先移到这里（同一块盘上，只是改名，瞬间完成），可以随时恢复原位；确认没问题后再彻底删除。
/// </summary>
/// <remarks>
/// 每批文件一个目录：<c>session.json</c> 记录时间和说明，<c>manifest.jsonl</c> 记录每个文件原来的位置。
/// 清单先写“准备移入”再移动文件，程序中途崩溃也不会丢失原始位置。
/// 旧版（脚本版）工具留在磁盘根目录 <c>.diskslim-quarantine</c> 里的批次也一并列出，可以同样恢复和彻底删除。
/// </remarks>
public sealed class QuarantineStore
{
    internal const string ManifestFileName = "manifest.jsonl";
    internal const string SessionFileName = "session.json";

    private static readonly JsonSerializerOptions LineOptions = new(DiskSlimJsonContext.Default.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="legacyRoot">旧版工具的隔离区；为 null 时不读取。</param>
    public QuarantineStore(string root, string? legacyRoot = null)
    {
        Root = Path.GetFullPath(root);
        LegacyRoot = legacyRoot is null ? null : Path.GetFullPath(legacyRoot);
    }

    public string Root { get; }

    /// <summary>旧版工具的隔离区（例如 <c>C:\.diskslim-quarantine</c>）；只读取和清空，不再往里放新文件。</summary>
    public string? LegacyRoot { get; }

    /// <summary>
    /// 指定磁盘上的隔离区。必须和被清理的文件在同一块盘上，移入才是瞬间完成的改名而不是复制。
    /// </summary>
    public static QuarantineStore ForDrive(string driveRoot) => new(DefaultRoot(driveRoot), LegacyRootFor(driveRoot));

    /// <summary>旧版（脚本版）工具在磁盘根目录下的隔离区。</summary>
    public static string LegacyRootFor(string driveRoot) =>
        Path.Join(Path.GetPathRoot(Path.GetFullPath(driveRoot)), LegacyQuarantine.DirectoryName);

    public static string DefaultRoot(string driveRoot)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.Equals(Path.GetPathRoot(local), Path.GetPathRoot(Path.GetFullPath(driveRoot)), StringComparison.OrdinalIgnoreCase)
            ? Path.Join(local, "DiskSlim", "Quarantine")
            : Path.Join(Path.GetPathRoot(Path.GetFullPath(driveRoot)), "DiskSlim.Quarantine");
    }

    /// <summary>开始新的一批。目录在第一个文件移入时才创建。</summary>
    public QuarantineWriter BeginSession(string description, DateTimeOffset now) =>
        new(this, $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}", description, now);

    /// <summary>列出隔离区中的所有批次（按时间从新到旧），已经全部恢复或为空的批次不列出。</summary>
    public IReadOnlyList<QuarantineSession> ListSessions()
    {
        var sessions = new List<QuarantineSession>();
        foreach (var directory in SessionDirectories(Root))
        {
            if (Load(directory) is { Items.Count: > 0 } session)
            {
                sessions.Add(session);
            }
        }

        foreach (var directory in SessionDirectories(LegacyRoot))
        {
            if (LegacyQuarantine.Load(directory) is { Items.Count: > 0 } session)
            {
                sessions.Add(session);
            }
        }

        return sessions.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public QuarantineSession? Find(string id) =>
        ListSessions().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把文件恢复到原来的位置。原位置已有同名文件或位于受保护位置时跳过，不会覆盖任何东西。
    /// </summary>
    /// <param name="indexes">只恢复这些序号；为 null 时恢复整批。</param>
    public RestoreResult Restore(QuarantineSession session, SafetyGuard guard, IReadOnlyCollection<int>? indexes = null)
    {
        EnsureInside(session.Directory);
        var problems = new List<QuarantineProblem>();
        int restored = 0;
        long restoredBytes = 0;
        foreach (var item in session.Items.Where(i => indexes is null || indexes.Contains(i.Index)))
        {
            if (guard.Check(item.OriginalPath) is { } reason)
            {
                problems.Add(new QuarantineProblem(item.OriginalPath, "原位置受保护，没有恢复：" + reason));
                continue;
            }

            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            {
                problems.Add(new QuarantineProblem(item.OriginalPath, "原位置已经有同名文件，没有覆盖"));
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (item.IsDirectory)
                {
                    Directory.Move(item.StoredPath, item.OriginalPath);
                }
                else
                {
                    File.Move(item.StoredPath, item.OriginalPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new QuarantineProblem(item.OriginalPath, ex.Message));
                continue;
            }

            // 旧版的清单不改写：移走了的项目下次读取时就不在了。
            if (!session.IsLegacy)
            {
                AppendRecord(session.Directory, new QuarantineRecord { Event = "restored", Index = item.Index, Time = DateTimeOffset.Now });
            }

            restored++;
            restoredBytes += item.Bytes;
        }

        DeleteIfEmpty(session);
        return new RestoreResult(restored, restoredBytes, problems);
    }

    /// <summary>彻底删除一批，释放空间。无法恢复。</summary>
    /// <exception cref="IOException">删除失败（通常是某个文件正被占用）。</exception>
    public void Purge(QuarantineSession session)
    {
        EnsureInside(session.Directory);
        var directory = new DirectoryInfo(session.Directory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{session.Directory} 是链接，为安全起见不删除");
        }

        ClearReadOnly(directory);
        directory.Delete(recursive: true);
        if (session.IsLegacy)
        {
            DeleteLegacyRootIfEmpty();
        }
    }

    /// <summary>彻底删除多批，每一步写入操作日志。某一批失败不影响其他批。</summary>
    public PurgeResult Purge(IEnumerable<QuarantineSession> sessions, IOperationLog log)
    {
        var list = sessions.ToList();
        log.Write(new LogEntry { Time = DateTimeOffset.Now, Event = "run-start", Detail = $"彻底删除隔离区中的 {list.Count} 批（{Root}）" });
        var problems = new List<QuarantineProblem>();
        int purged = 0;
        long bytes = 0;
        foreach (var session in list)
        {
            try
            {
                Purge(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new QuarantineProblem(session.Directory, ex.Message));
                log.Write(new LogEntry { Time = DateTimeOffset.Now, Event = "failed", Path = session.Directory, Bytes = session.TotalBytes, Detail = ex.Message });
                continue;
            }

            purged++;
            bytes += session.TotalBytes;
            log.Write(new LogEntry { Time = DateTimeOffset.Now, Event = "purged-quarantine", Path = session.Directory, Bytes = session.TotalBytes, Detail = $"{session.Items.Count} 个文件，{session.CreatedAt:yyyy-MM-dd HH:mm} 移入" });
        }

        log.Write(new LogEntry { Time = DateTimeOffset.Now, Event = "run-end", Bytes = bytes, Detail = $"成功 {purged} 批，失败 {problems.Count} 批" });
        return new PurgeResult(purged, bytes, problems);
    }

    internal static void AppendRecord(string sessionDirectory, QuarantineRecord record)
    {
        var line = JsonSerializer.Serialize(record, LineOptions.GetTypeInfo(typeof(QuarantineRecord)));
        File.AppendAllText(Path.Join(sessionDirectory, ManifestFileName), line + Environment.NewLine, new UTF8Encoding(false));
    }

    internal static void WriteSessionInfo(string sessionDirectory, QuarantineSessionInfo info) =>
        File.WriteAllText(Path.Join(sessionDirectory, SessionFileName),
            JsonSerializer.Serialize(info, LineOptions.GetTypeInfo(typeof(QuarantineSessionInfo))), new UTF8Encoding(false));

    private static QuarantineSession? Load(string directory)
    {
        QuarantineSessionInfo? info;
        List<QuarantineRecord> records = [];
        try
        {
            info = JsonSerializer.Deserialize(File.ReadAllText(Path.Join(directory, SessionFileName)), DiskSlimJsonContext.Default.QuarantineSessionInfo);
            var manifest = Path.Join(directory, ManifestFileName);
            if (File.Exists(manifest))
            {
                foreach (var line in File.ReadLines(manifest))
                {
                    if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize(line, DiskSlimJsonContext.Default.QuarantineRecord) is { } record)
                    {
                        records.Add(record);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (info is null)
        {
            return null;
        }

        // 只保留“移入了、没失败、没恢复、文件还在”的条目。
        var gone = records.Where(r => r.Event is "failed" or "restored").Select(r => r.Index).ToHashSet();
        var items = new List<QuarantineItem>();
        foreach (var record in records.Where(r => r.Event == "added" && !gone.Contains(r.Index)))
        {
            if (record.OriginalPath is null || record.StoredName is null)
            {
                continue;
            }

            var stored = Path.Join(directory, record.StoredName);
            if (File.Exists(stored))
            {
                items.Add(new QuarantineItem(record.Index, record.OriginalPath, stored, record.Bytes ?? new FileInfo(stored).Length, record.Rule));
            }
        }

        return new QuarantineSession(info.Id, directory, info.CreatedAt, info.Description, items);
    }

    private void DeleteIfEmpty(QuarantineSession session)
    {
        var reloaded = session.IsLegacy ? LegacyQuarantine.Load(session.Directory) : Load(session.Directory);
        if (reloaded is { Items.Count: 0 })
        {
            try
            {
                Directory.Delete(session.Directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留着空目录也无妨，下次列出时会被忽略。
            }
        }

        if (session.IsLegacy)
        {
            DeleteLegacyRootIfEmpty();
        }
    }

    /// <summary>旧版隔离区清空后把它的根目录也删掉，免得在磁盘根目录留下一个空文件夹。</summary>
    private void DeleteLegacyRootIfEmpty()
    {
        try
        {
            if (LegacyRoot is { } legacy && Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留着空目录也无妨。
        }
    }

    private static IEnumerable<string> SessionDirectories(string? root)
    {
        if (root is null || !Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>去掉只读属性，否则删不掉。不进入链接：链接只删除它本身，不碰它指向的内容。</summary>
    private static void ClearReadOnly(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var attributes = entry.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes = attributes & ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo child)
            {
                ClearReadOnly(child);
            }
        }
    }

    private void EnsureInside(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!IsInside(full, Root) && !(LegacyRoot is { } legacy && IsInside(full, legacy)))
        {
            throw new InvalidOperationException($"{directory} 不在隔离区 {Root} 中");
        }
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>把文件移入隔离区中的一批。一次清理使用一个。</summary>
public sealed class QuarantineWriter
{
    private readonly QuarantineStore _store;
    private readonly string _description;
    private readonly DateTimeOffset _createdAt;
    private readonly Lock _lock = new();
    private int _nextIndex;
    private int _count;
    private bool _created;

    internal QuarantineWriter(QuarantineStore store, string id, string description, DateTimeOffset createdAt)
    {
        _store = store;
        Id = id;
        _description = description;
        _createdAt = createdAt;
        SessionDirectory = Path.Join(store.Root, id);
    }

    public string Id { get; }

    public string SessionDirectory { get; }

    /// <summary>成功移入的文件数。</summary>
    public int Count => _count;

    /// <summary>把文件移入隔离区。</summary>
    /// <param name="anchor">文件所在的清理目标；给出时先按句柄核对文件的实际位置，再通过同一个句柄移动。</param>
    /// <exception cref="IOException">移动失败（包括实际位置不在目标里），文件仍在原处。</exception>
    /// <exception cref="UnauthorizedAccessException">没有权限，文件仍在原处。</exception>
    public void Move(string path, long bytes, string? rule, Platform.PathAnchor? anchor = null)
    {
        lock (_lock)
        {
            EnsureCreated();

            // 每次尝试都占用一个新序号，失败的也不复用：清单里标记为 failed 的序号不能再对应别的文件。
            var index = ++_nextIndex;
            var stored = $"{index:D6}{SafeExtension(path)}";

            // 先记下原始位置，再移动：即使移动过程中程序崩溃，也能从清单找回。
            QuarantineStore.AppendRecord(SessionDirectory, new QuarantineRecord
            {
                Event = "added",
                Index = index,
                OriginalPath = path,
                StoredName = stored,
                Bytes = bytes,
                Rule = rule,
                Time = DateTimeOffset.Now,
            });

            try
            {
                var destination = Path.Join(SessionDirectory, stored);
                if (anchor is null)
                {
                    File.Move(path, destination);
                }
                else
                {
                    Platform.HandleOperations.MoveFile(path, anchor, destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                QuarantineStore.AppendRecord(SessionDirectory, new QuarantineRecord { Event = "failed", Index = index, Time = DateTimeOffset.Now });
                throw;
            }

            _count++;
        }
    }

    private void EnsureCreated()
    {
        if (_created)
        {
            return;
        }

        Directory.CreateDirectory(SessionDirectory);
        QuarantineStore.WriteSessionInfo(SessionDirectory, new QuarantineSessionInfo { Id = Id, CreatedAt = _createdAt, Description = _description });
        _created = true;
    }

    /// <summary>保留原来的扩展名（方便在资源管理器里认出文件类型），但只保留安全的字符。</summary>
    private static string SafeExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length is > 1 and <= 16 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : "";
    }
}
