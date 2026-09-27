using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Duplicates;

public sealed record DuplicateSearchOptions
{
    /// <summary>比这小的文件不查（小文件多、省下的空间少）。</summary>
    public long MinFileSize { get; init; } = 1024 * 1024;

    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>这些区域里的文件只列出、不允许移除（例如聊天软件的数据目录）：路径 → 说明。</summary>
    public IReadOnlyList<ProtectedArea> ProtectedAreas { get; init; } = [];

    public SafetyGuard? Guard { get; init; }

    /// <summary>不进入这些目录（完整路径）。</summary>
    public IReadOnlyList<string> SkipDirectories { get; init; } = [];

    /// <summary>不进入这些名字的目录（不区分大小写），例如 node_modules。</summary>
    public IReadOnlySet<string> SkipDirectoryNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>不进入名字以“.”开头的目录（.git、.venv、.nuget 这类工具数据）。</summary>
    public bool SkipDotDirectories { get; init; }

    /// <summary>这些扩展名的文件只列出、不允许移除（程序库：不同软件自带同一个 DLL 很正常，移走会导致软件打不开）。</summary>
    public IReadOnlySet<string> ProtectedExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>这些目录里的重复文件视为“明显的副本”（例如下载文件夹：同一个文件下载了两次）。</summary>
    public IReadOnlyList<string> CopyLocations { get; init; } = [];

    /// <summary>
    /// 适合普通用户的默认设置：跳过软件数据（AppData、ProgramData）、开发工具目录（.git、node_modules……），
    /// 聊天记录等“仅供参考”的位置和程序库文件只列出、不能移除。
    /// </summary>
    public static DuplicateSearchOptions ForCurrentUser(RuleSet rules, long minFileSize = 1024 * 1024) => new()
    {
        MinFileSize = minFileSize,
        Guard = SafetyGuard.ForCurrentUser(),
        ProtectedAreas = DuplicateFinder.ProtectedAreasFrom(rules),
        SkipDirectories =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ],
        SkipDirectoryNames = new HashSet<string>(["node_modules", "__pycache__", "site-packages", "$Recycle.Bin", "System Volume Information"], StringComparer.OrdinalIgnoreCase),
        SkipDotDirectories = true,
        ProtectedExtensions = new HashSet<string>([".dll", ".sys", ".drv", ".ocx", ".so", ".pyd", ".node", ".mui", ".winmd"], StringComparer.OrdinalIgnoreCase),
        CopyLocations = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")],
    };
}

public sealed record ProtectedArea(string Path, string Reason);

public sealed record DuplicateFile(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc, DateTimeOffset CreationTimeUtc)
{
    /// <summary>不为 null 时这个文件不能被移除，只能保留。</summary>
    public string? ProtectedReason { get; init; }

    public bool IsProtected => ProtectedReason is not null;

    /// <summary>
    /// 明显是副本：文件名带“(1)”“副本”“- Copy”这类标记，或者在下载文件夹里（同一个文件下载了两次）。
    /// 只有这样的文件才会被建议移除；其他重复文件只列出，由用户自己决定。
    /// </summary>
    public bool LooksLikeCopy { get; init; }
}

/// <summary>内容完全相同的一组文件。</summary>
public sealed record DuplicateGroup(string Hash, long FileSize, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>只保留一份时能省下的空间。</summary>
    public long WastedBytes => FileSize * (Files.Count - 1);

    public bool HasProtectedFiles => Files.Any(f => f.IsProtected);

    /// <summary>
    /// 建议保留的文件：优先不像副本的，其次最早创建的（复制出来的副本创建时间更晚），再其次路径最短的。
    /// </summary>
    public DuplicateFile SuggestedKeeper => Files
        .OrderBy(f => f.LooksLikeCopy)
        .ThenBy(f => f.CreationTimeUtc)
        .ThenBy(f => f.Path.Length)
        .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .First();

    /// <summary>
    /// 建议移除的文件：保留那份以外、明显是副本的文件。组里有受保护的文件时不给建议（全部保留）。
    /// 其他重复文件（例如项目目录里被引用的资源）不自动建议，由用户在列表里自己勾选。
    /// </summary>
    public IReadOnlyList<DuplicateFile> SuggestedRemovals
    {
        get
        {
            if (HasProtectedFiles)
            {
                return [];
            }

            var keeper = SuggestedKeeper;
            return Files.Where(f => !ReferenceEquals(f, keeper) && f.LooksLikeCopy).ToList();
        }
    }
}

public sealed record DuplicateSearchResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }

    public long FilesScanned { get; init; }

    /// <summary>大小相同、需要进一步比较的文件数。</summary>
    public long CandidateFiles { get; init; }

    public long BytesHashed { get; init; }

    /// <summary>硬链接（同一个物理文件的多个路径），不算重复。</summary>
    public long HardLinksIgnored { get; init; }

    public long UnreadableFiles { get; init; }

    public double ElapsedSeconds { get; init; }

    public long WastedBytes => Groups.Sum(g => g.WastedBytes);
}

/// <summary>
/// 查找内容完全相同的文件。只读取文件，不修改任何东西。
/// </summary>
/// <remarks>
/// 按成本从低到高逐步缩小范围：先按大小分组，再比较文件头尾各 16 KB 的哈希，最后才对剩下的文件计算完整的 SHA-256。
/// 同一个物理文件的多个硬链接只算一份。不进入链接、云盘仅在线目录和安全防线里的禁区。
/// </remarks>
public sealed partial class DuplicateFinder
{
    /// <summary>规则文件中用户个人文件的类别名。</summary>
    public const string UserFilesCategory = "用户文件";

    private const int EdgeBytes = 16 * 1024;
    private const int BufferSize = 1024 * 1024;

    private volatile string _stage = "";
    private long _filesScanned;
    private long _bytesHashed;

    public string Stage => _stage;

    public long FilesScanned => Interlocked.Read(ref _filesScanned);

    public long BytesHashed => Interlocked.Read(ref _bytesHashed);

    public DuplicateSearchResult Find(IReadOnlyList<string> roots, DuplicateSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DuplicateSearchOptions();
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Exchange(ref _filesScanned, 0);
        Interlocked.Exchange(ref _bytesHashed, 0);
        var normalizedRoots = NormalizeRoots(roots);

        // 1. 列出文件，按大小分组。
        _stage = "列出文件";
        var bySize = new Dictionary<long, List<FileCandidate>>();
        foreach (var root in normalizedRoots)
        {
            foreach (var file in Enumerate(root, options, cancellationToken))
            {
                Interlocked.Increment(ref _filesScanned);
                if (!bySize.TryGetValue(file.Bytes, out var list))
                {
                    bySize[file.Bytes] = list = [];
                }

                list.Add(file);
            }
        }

        var candidates = bySize.Values.Where(g => g.Count > 1).SelectMany(g => g).ToList();

        // 2. 读取文件身份（去掉硬链接）和头尾哈希。
        _stage = "比较文件头尾";
        long unreadable = 0;
        var probed = new ConcurrentBag<ProbedFile>();
        Parallel.ForEach(candidates, ParallelOptionsFor(options, cancellationToken), file =>
        {
            if (Probe(file) is { } result)
            {
                probed.Add(result);
            }
            else
            {
                Interlocked.Increment(ref unreadable);
            }
        });

        long hardLinks = 0;
        var distinct = new List<ProbedFile>();
        foreach (var sameIdentity in probed.GroupBy(p => (p.Identity.VolumeSerial, p.Identity.FileIndex)))
        {
            // 同一个物理文件的多个硬链接：只保留路径排序最前的一个参与比较。
            var ordered = sameIdentity.OrderBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase).ToList();
            distinct.Add(ordered[0]);
            hardLinks += ordered.Count - 1;
        }

        var partialGroups = distinct
            .GroupBy(p => (p.File.Bytes, p.EdgeHash))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        // 3. 完整哈希。
        _stage = "计算完整哈希";
        var hashed = new ConcurrentBag<(ProbedFile File, string Hash)>();
        Parallel.ForEach(partialGroups, ParallelOptionsFor(options, cancellationToken), file =>
        {
            if (FullHash(file.File.Path, cancellationToken) is { } hash)
            {
                hashed.Add((file, hash));
            }
            else
            {
                Interlocked.Increment(ref unreadable);
            }
        });

        _stage = "整理结果";
        var protectedAreas = options.ProtectedAreas;
        var groups = hashed
            .GroupBy(h => (h.File.File.Bytes, h.Hash))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                g.Key.Hash,
                g.Key.Bytes,
                g.Select(h => Annotate(h.File.File.ToDuplicate(), protectedAreas, options))
                    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _stage = "完成";
        return new DuplicateSearchResult
        {
            Groups = groups,
            Roots = normalizedRoots,
            FilesScanned = FilesScanned,
            CandidateFiles = candidates.Count,
            BytesHashed = BytesHashed,
            HardLinksIgnored = hardLinks,
            UnreadableFiles = Interlocked.Read(ref unreadable),
            ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
        };
    }

    /// <summary>
    /// “仅供参考”的规则命中的软件数据和系统文件，作为受保护区域（里面的重复文件只列出，不能移除）：
    /// 例如聊天记录里的文件被软件内部引用，删掉副本会导致软件里打不开。
    /// “用户文件”类（下载、桌面）不在此列——那里的重复文件正是用户想整理的，由用户自己决定。
    /// </summary>
    public static IReadOnlyList<ProtectedArea> ProtectedAreasFrom(RuleSet rules) => rules.Rules
        .Where(r => r.Risk == RiskLevel.High && r.Category != UserFilesCategory)
        .SelectMany(r => r.Paths.Where(p => !PathExpander.IsSpecial(p)).SelectMany(p => PathExpander.Expand(p))
            .Select(path => new ProtectedArea(path, $"属于“{r.Name}”，请在对应软件里处理")))
        .ToList();

    private static List<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var full = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)
            .ToList();

        // 去掉包含在其他根目录里的根目录，免得同一个文件被列两次。
        var result = new List<string>();
        foreach (var root in full)
        {
            if (!result.Any(r => PathExpander.IsSameOrInside(root, r)))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private static IEnumerable<FileCandidate> Enumerate(string root, DuplicateSearchOptions options, CancellationToken cancellationToken)
    {
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            BufferSize = 32 * 1024,
        };
        var minSize = options.MinFileSize;
        var guard = options.Guard;
        var skipNames = options.SkipDirectoryNames;
        var skipDot = options.SkipDotDirectories;
        var skipPaths = options.SkipDirectories
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enumerable = new FileSystemEnumerable<FileCandidate>(root,
            (ref FileSystemEntry e) => new FileCandidate(e.ToFullPath(), e.Length, e.LastWriteTimeUtc, e.CreationTimeUtc),
            enumerationOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && e.Length >= minSize
                && (e.Attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) == 0,
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                if ((e.Attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) != 0)
                {
                    return false;
                }

                if ((skipDot && e.FileName.StartsWith('.')) || skipNames.Contains(e.FileName.ToString()))
                {
                    return false;
                }

                var full = e.ToFullPath();
                return !skipPaths.Contains(full) && (guard is null || !guard.IsForbiddenZone(full));
            },
        };

        foreach (var file in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private ProbedFile? Probe(FileCandidate file)
    {
        try
        {
            using var handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (FileIdentity.Of(handle) is not { } identity)
            {
                return null;
            }

            // 头尾各 16 KB（小文件就是整个文件）。
            var buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes * 2);
            try
            {
                var headLength = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(EdgeBytes, file.Bytes)), 0);
                var tailLength = 0;
                if (file.Bytes > EdgeBytes)
                {
                    // 尾部紧接在头部后面读；文件不到 32 KB 时尾部从 16 KB 处开始，不和头部重叠。
                    var tailStart = Math.Max(EdgeBytes, file.Bytes - EdgeBytes);
                    tailLength = RandomAccess.Read(handle, buffer.AsSpan(headLength, (int)(file.Bytes - tailStart)), tailStart);
                }

                Interlocked.Add(ref _bytesHashed, headLength + tailLength);
                var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, headLength + tailLength)));
                return new ProbedFile(file, identity, hash);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? FullHash(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, buffer.AsSpan(0, BufferSize), offset)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                offset += read;
                Interlocked.Add(ref _bytesHashed, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DuplicateFile Annotate(DuplicateFile file, IReadOnlyList<ProtectedArea> areas, DuplicateSearchOptions options)
    {
        if (options.Guard?.Check(file.Path) is { } reason)
        {
            return file with { ProtectedReason = "位于受保护的位置：" + reason };
        }

        if (options.ProtectedExtensions.Contains(Path.GetExtension(file.Path)))
        {
            return file with { ProtectedReason = "程序库文件：不同软件常常自带同一份，移走可能导致软件打不开" };
        }

        // 备份就是有意保留的副本：原件以后变了，备份里还是旧的状态。
        var directory = Path.GetDirectoryName(file.Path) ?? "";
        if (BackupFolder().IsMatch(directory))
        {
            return file with { ProtectedReason = "看起来是备份，保留" };
        }

        file = file with
        {
            LooksLikeCopy = CopyName().IsMatch(Path.GetFileNameWithoutExtension(file.Path))
                || options.CopyLocations.Any(location => PathExpander.IsSameOrInside(file.Path, location)),
        };

        foreach (var area in areas)
        {
            if (PathExpander.IsSameOrInside(file.Path, area.Path))
            {
                return file with { ProtectedReason = area.Reason };
            }
        }

        return file;
    }

    /// <summary>“报告 (1)”“报告 - 副本”“report - Copy”“copy of report”“report_copy”这类副本文件名。</summary>
    [GeneratedRegex(@"(\(\d{1,3}\)|（\d{1,3}）|副本|复件|[\s_-]copy(\s*\d+)?|^copy of\s)\s*$|^copy of\s", RegexOptions.IgnoreCase)]
    private static partial Regex CopyName();

    /// <summary>路径中带 backup / 备份 的目录。</summary>
    [GeneratedRegex(@"(^|\\)[^\\]*(backup|备份)[^\\]*(\\|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BackupFolder();

    private static ParallelOptions ParallelOptionsFor(DuplicateSearchOptions options, CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = cancellationToken };

    private readonly record struct FileCandidate(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc, DateTimeOffset CreationTimeUtc)
    {
        public DuplicateFile ToDuplicate() => new(Path, Bytes, LastWriteTimeUtc, CreationTimeUtc);
    }

    private sealed record ProbedFile(FileCandidate File, FileIdentity Identity, string EdgeHash);
}
