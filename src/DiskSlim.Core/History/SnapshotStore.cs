using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.History;

/// <summary>
/// 空间快照的存放位置：每块磁盘一个目录，里面是 <c>时间.json.gz</c> 快照和一个 <c>index.jsonl</c> 用量索引。
/// </summary>
/// <remarks>
/// 保留策略：最近 2 天的快照全部保留；90 天以内每天保留最后一份；更早的删除。
/// 索引只追加不改写，所以删掉的快照仍然能在趋势图里显示用量数字。
/// </remarks>
public sealed partial class SnapshotStore
{
    internal const string IndexFileName = "index.jsonl";
    private const string Extension = ".json.gz";

    /// <summary>设置这个环境变量可以把快照放到别的目录（用于测试和演示，不影响真实记录）。</summary>
    public const string DirectoryVariable = "DISKSLIM_HISTORY_DIR";

    internal static readonly TimeSpan KeepAll = TimeSpan.FromDays(2);
    internal static readonly TimeSpan KeepDaily = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions Options = new(DiskSlimJsonContext.Default.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SnapshotStore(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public static string DefaultRoot => Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } custom
        ? custom
        : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSlim", "history");

    public static SnapshotStore Default => new(DefaultRoot);

    /// <summary>保存快照、追加索引，再按保留策略删除旧快照。</summary>
    public SnapshotSummary Save(SpaceSnapshot snapshot)
    {
        var directory = DriveDirectory(snapshot.Drive);
        Directory.CreateDirectory(directory);

        var baseName = snapshot.Time.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = baseName + Extension;
        for (var i = 2; File.Exists(Path.Join(directory, name)); i++)
        {
            name = $"{baseName}-{i}{Extension}";
        }

        // 先写临时文件再改名：别的进程只会看到完整的快照。
        var path = Path.Join(directory, name);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            JsonSerializer.Serialize(gzip, snapshot, Options.GetTypeInfo(typeof(SpaceSnapshot)));
        }

        File.Move(temp, path);

        var summary = SnapshotSummary.Of(snapshot, name);
        AppendIndex(directory, summary);
        Prune(snapshot.Drive, snapshot.Time);
        return summary;
    }

    /// <summary>这块磁盘的扫描记录，按时间从旧到新。明细已被删除的记录 <see cref="SnapshotSummary.File"/> 为 null。</summary>
    public IReadOnlyList<SnapshotSummary> History(string drive)
    {
        var directory = DriveDirectory(drive);
        var index = Path.Join(directory, IndexFileName);
        if (!File.Exists(index))
        {
            return [];
        }

        string[] lines;
        try
        {
            using var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<SnapshotSummary>();
        foreach (var line in lines)
        {
            SnapshotSummary? entry;
            try
            {
                entry = (SnapshotSummary?)JsonSerializer.Deserialize(line, Options.GetTypeInfo(typeof(SnapshotSummary)));
            }
            catch (JsonException)
            {
                continue; // 写到一半的行（例如断电）直接跳过
            }

            if (entry is null)
            {
                continue;
            }

            if (entry.File is { } file && !(IsSnapshotName(file) && File.Exists(Path.Join(directory, file))))
            {
                entry = entry with { File = null };
            }

            entries.Add(entry);
        }

        return entries.OrderBy(e => e.Time).ToList();
    }

    /// <summary>读取一份快照；文件已删除或损坏时返回 null。</summary>
    public SpaceSnapshot? Load(string drive, SnapshotSummary summary)
    {
        if (summary.File is not { } file || !IsSnapshotName(file))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(Path.Join(DriveDirectory(drive), file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return (SpaceSnapshot?)JsonSerializer.Deserialize(gzip, Options.GetTypeInfo(typeof(SpaceSnapshot)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 <paramref name="current"/> 和大约 <paramref name="age"/> 以前的快照比较（见 <see cref="GrowthAnalyzer.PickBaseline"/>）。
    /// 没有更早的快照时返回 null。
    /// </summary>
    public GrowthReport? CompareWithPast(SpaceSnapshot current, TimeSpan age, long minBytes = GrowthAnalyzer.DefaultMinBytes, int top = 15)
    {
        var history = History(current.Drive);
        var baseline = GrowthAnalyzer.PickBaseline(history, current.Time, age);
        return baseline is not null && Load(current.Drive, baseline) is { } old
            ? GrowthAnalyzer.Compare(old, current, minBytes, top)
            : null;
    }

    /// <summary>按保留策略删除旧快照，返回删除的文件名。只会删除本工具命名格式的快照文件。</summary>
    internal IReadOnlyList<string> Prune(string drive, DateTimeOffset now)
    {
        var directory = DriveDirectory(drive);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var snapshots = Directory.EnumerateFiles(directory, "*" + Extension)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Select(name => (Name: name, Time: ParseTime(name)))
            .Where(s => s.Time is not null)
            .OrderByDescending(s => s.Time)
            .ThenByDescending(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var keptDays = new HashSet<DateOnly>();
        var deleted = new List<string>();
        foreach (var (name, time) in snapshots)
        {
            var age = now.LocalDateTime - time!.Value;
            var day = DateOnly.FromDateTime(time.Value);
            var keep = age <= KeepAll || (age <= KeepDaily && keptDays.Add(day));
            if (age <= KeepAll)
            {
                keptDays.Add(day);
            }

            if (keep)
            {
                continue;
            }

            try
            {
                File.Delete(Path.Join(directory, name));
                deleted.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 正被另一个进程读取，下次再删。
            }
        }

        return deleted;
    }

    private string DriveDirectory(string drive)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(drive));
        if (root is not { Length: >= 2 } || root[1] != ':' || !char.IsAsciiLetter(root[0]))
        {
            throw new ArgumentException($"只支持带盘符的磁盘：{drive}", nameof(drive));
        }

        return Path.Join(Root, char.ToUpperInvariant(root[0]).ToString());
    }

    private static void AppendIndex(string directory, SnapshotSummary summary)
    {
        var line = JsonSerializer.Serialize(summary, Options.GetTypeInfo(typeof(SnapshotSummary))) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        // 图形界面和定时任务可能同时写：一次写入一整行，并在文件被占用时稍等重试。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path.Join(directory, IndexFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static bool IsSnapshotName(string name) => SnapshotName().IsMatch(name);

    private static DateTime? ParseTime(string name) =>
        IsSnapshotName(name) && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    [GeneratedRegex(@"^\d{8}-\d{6}(-\d+)?\.json\.gz$")]
    private static partial Regex SnapshotName();
}
