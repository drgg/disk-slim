using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Archives;

/// <summary>凭什么认为一个压缩包已经解压过。</summary>
public enum ExtractionEvidence
{
    /// <summary>压缩包里的每个文件都在解压出来的文件夹里找到了，大小也一致（只有 zip 能这样逐个核对）。</summary>
    Verified,

    /// <summary>旁边有同名文件夹，而且不比压缩包小。rar、7z 读不出文件列表，只能这样判断，请自己确认。</summary>
    SameNameFolder,

    /// <summary>只找到了一部分文件，或者同名文件夹比压缩包小很多：可能没解压完，或者解压后删改过。</summary>
    Partial,
}

/// <summary>压缩包的一个分卷（单卷的压缩包只有一个）。</summary>
public sealed record ArchivePart(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc);

/// <summary>一个看起来已经解压过的压缩包。</summary>
/// <param name="ExtractedTo">解压出来的文件都在这个文件夹里。</param>
/// <param name="EntryCount">zip 里的文件数；读不出文件列表时为 0。</param>
/// <param name="MatchedEntries">在 <paramref name="ExtractedTo"/> 里找到、大小一致的文件数。</param>
public sealed record ExtractedArchive(
    IReadOnlyList<ArchivePart> Parts,
    string ExtractedTo,
    ExtractionEvidence Evidence,
    int EntryCount,
    int MatchedEntries,
    long ExtractedBytes)
{
    /// <summary>第一卷（或唯一的一个文件）。</summary>
    public string Path => Parts[0].Path;

    public long Bytes => Parts.Sum(p => p.Bytes);

    /// <summary>只有逐个文件核对过的才建议移除；其他的只列出，由用户自己判断。</summary>
    public bool SuggestRemoval => Evidence == ExtractionEvidence.Verified;
}

public sealed record ArchiveSearchOptions
{
    public const long DefaultMinSize = 50L << 20;

    /// <summary>比这小的压缩包不查（省不了多少空间）。分卷压缩包按所有分卷的合计算。</summary>
    public long MinArchiveSize { get; init; } = DefaultMinSize;

    public SafetyGuard? Guard { get; init; }

    /// <summary>不进入这些目录（完整路径）。</summary>
    public IReadOnlyList<string> SkipDirectories { get; init; } = [];

    /// <summary>
    /// 适合普通用户的默认设置：不进入软件数据（AppData、ProgramData）、Windows 目录和安全防线里的禁区。
    /// </summary>
    public static ArchiveSearchOptions ForCurrentUser(long minArchiveSize = DefaultMinSize) => new()
    {
        MinArchiveSize = minArchiveSize,
        Guard = SafetyGuard.ForCurrentUser(),
        SkipDirectories =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ],
    };

    /// <summary>
    /// 不指定位置时查的地方：用户的下载和桌面，以及每块本地硬盘上百度网盘、迅雷等默认的下载文件夹。
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            System.IO.Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        foreach (var drive in InstalledApps.FixedDriveRoots())
        {
            roots.AddRange(new[] { "BaiduNetdiskDownload", "迅雷下载", "Downloads", "下载" }.Select(name => System.IO.Path.Join(drive, name)));
        }

        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed record ArchiveSearchResult
{
    public required IReadOnlyList<ExtractedArchive> Archives { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>检查过的压缩包个数（不管有没有解压过）。</summary>
    public long ArchivesChecked { get; init; }

    public double ElapsedSeconds { get; init; }

    /// <summary>建议移除的压缩包合计大小。</summary>
    public long SuggestedBytes => Archives.Where(a => a.SuggestRemoval).Sum(a => a.Bytes);
}

/// <summary>
/// 找出已经解压过的压缩包：旁边（或同一个文件夹里）已经有解压出来的文件，压缩包本身就可以删了。只读取，不修改任何东西。
/// </summary>
/// <remarks>
/// zip 可以读出文件列表（不用解压、不用密码），逐个核对解压出来的文件是否都在、大小是否一致；
/// rar 和 7z 读不出文件列表，只能看旁边有没有同名、而且不比压缩包小的文件夹，这种只列出、不建议移除。
/// 分卷压缩包（.part1.rar、.7z.001……）作为一个整体处理。
/// </remarks>
public sealed partial class ExtractedArchiveFinder
{
    /// <summary>同名文件夹至少要有压缩包的这么大，才认为解压完了（压缩后变小是常态，解压出来只会更大或差不多）。</summary>
    private const double MinExtractedRatio = 0.9;

    private volatile string _stage = "";
    private long _archivesChecked;

    public string Stage => _stage;

    public long ArchivesChecked => Interlocked.Read(ref _archivesChecked);

    public ArchiveSearchResult Find(IReadOnlyList<string> roots, ArchiveSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ArchiveSearchOptions();
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Exchange(ref _archivesChecked, 0);
        var normalizedRoots = NormalizeRoots(roots);

        _stage = "查找压缩包";
        var files = normalizedRoots.SelectMany(r => Enumerate(r, options, cancellationToken)).ToList();

        _stage = "核对解压出来的文件";
        var found = new List<ExtractedArchive>();
        foreach (var group in GroupVolumes(files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Parts.Sum(p => p.Bytes) < options.MinArchiveSize)
            {
                continue;
            }

            Interlocked.Increment(ref _archivesChecked);
            if (Check(group, cancellationToken) is { } archive)
            {
                found.Add(archive);
            }
        }

        _stage = "完成";
        return new ArchiveSearchResult
        {
            Archives = found
                .OrderBy(a => a.Evidence)
                .ThenByDescending(a => a.Bytes)
                .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Roots = normalizedRoots,
            ArchivesChecked = ArchivesChecked,
            ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
        };
    }

    /// <summary>一个压缩包（可能有多个分卷）：解压出来的文件夹默认和它同名。</summary>
    internal sealed record VolumeGroup(string Stem, string Directory, IReadOnlyList<ArchivePart> Parts, ArchiveKind Kind);

    internal enum ArchiveKind
    {
        Zip,
        SplitZip,
        Rar,
        SevenZip,
    }

    /// <summary>
    /// 把分卷归到一起，找出每个压缩包的第一卷和它的“名字”（解压出来的文件夹通常就叫这个名字）。
    /// 只有第二卷之后的分卷（缺了第一卷）不会被当成压缩包。
    /// </summary>
    internal static IEnumerable<VolumeGroup> GroupVolumes(IEnumerable<ArchivePart> files)
    {
        var byDirectory = files.GroupBy(f => System.IO.Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var directory in byDirectory)
        {
            var parts = directory.ToList();
            foreach (var part in parts)
            {
                var name = System.IO.Path.GetFileName(part.Path);
                if (RarVolume().Match(name) is { Success: true } rarVolume)
                {
                    // name.part1.rar（第一卷）+ name.part2.rar ……
                    if (int.Parse(rarVolume.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) == 1)
                    {
                        var stem = rarVolume.Groups[1].Value;
                        yield return new VolumeGroup(stem, directory.Key, Volumes(parts, p => RarVolume().Match(p) is { Success: true } m && Same(m.Groups[1].Value, stem)), ArchiveKind.Rar);
                    }
                }
                else if (NumberedVolume().Match(name) is { Success: true } numbered)
                {
                    // name.7z.001 / name.zip.001（第一卷）+ .002 ……
                    if (int.Parse(numbered.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) == 1)
                    {
                        var stem = numbered.Groups[1].Value;
                        var extension = numbered.Groups[2].Value;
                        var kind = extension.Equals("7z", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.SevenZip
                            : extension.Equals("zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.SplitZip
                            : ArchiveKind.Rar;
                        yield return new VolumeGroup(stem, directory.Key,
                            Volumes(parts, p => NumberedVolume().Match(p) is { Success: true } m && Same(m.Groups[1].Value, stem) && Same(m.Groups[2].Value, extension)), kind);
                    }
                }
                else if (OldStyleVolume().IsMatch(name))
                {
                    // name.r00、name.z01：属于 name.rar / name.zip，跟着它一起处理。
                    continue;
                }
                else
                {
                    var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
                    var stem = System.IO.Path.GetFileNameWithoutExtension(name);
                    var extra = extension is ".rar" or ".zip"
                        ? parts.Where(p => OldStyleVolume().Match(System.IO.Path.GetFileName(p.Path)) is { Success: true } m
                            && Same(m.Groups[1].Value, stem) && char.ToLowerInvariant(m.Groups[2].Value[0]) == extension[1]).ToList()
                        : [];
                    var kind = extension switch
                    {
                        ".zip" => extra.Count > 0 ? ArchiveKind.SplitZip : ArchiveKind.Zip,
                        ".rar" => ArchiveKind.Rar,
                        _ => ArchiveKind.SevenZip,
                    };
                    yield return new VolumeGroup(stem, directory.Key, [part, .. extra.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)], kind);
                }
            }
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static List<ArchivePart> Volumes(List<ArchivePart> parts, Func<string, bool> belongs) => parts
            .Where(p => belongs(System.IO.Path.GetFileName(p.Path)))
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ExtractedArchive? Check(VolumeGroup group, CancellationToken cancellationToken)
    {
        var sameName = System.IO.Path.Join(group.Directory, group.Stem);
        var hasFolder = Directory.Exists(sameName) && (File.GetAttributes(sameName) & FileAttributes.ReparsePoint) == 0;

        if (group.Kind == ArchiveKind.Zip && ReadZipEntries(group.Parts[0].Path) is { Count: > 0 } entries)
        {
            // 解压到同名文件夹里，或者直接解压在压缩包旁边；压缩包里只有一个顶层文件夹时，有的解压软件会去掉这一层。
            var candidates = new List<(string Root, string? StripPrefix)> { (group.Directory, null) };
            if (hasFolder)
            {
                candidates.Insert(0, (sameName, null));
                if (SingleTopFolder(entries) is { } top)
                {
                    candidates.Add((sameName, top));
                }
            }

            var best = candidates
                .Select(c => (c.Root, Matched: CountMatches(entries, c.Root, c.StripPrefix, cancellationToken)))
                .OrderByDescending(c => c.Matched.Count)
                .First();
            if (best.Matched.Count == 0)
            {
                return null;
            }

            var evidence = best.Matched.Count == entries.Count ? ExtractionEvidence.Verified : ExtractionEvidence.Partial;
            return new ExtractedArchive(group.Parts, best.Root, evidence, entries.Count, best.Matched.Count, best.Matched.Bytes);
        }

        // rar、7z、分卷 zip 和读不了的 zip：只能看有没有同名文件夹。
        if (!hasFolder)
        {
            return null;
        }

        var folderBytes = FolderSize(sameName, cancellationToken);
        var archiveBytes = group.Parts.Sum(p => p.Bytes);
        var nameEvidence = folderBytes >= archiveBytes * MinExtractedRatio ? ExtractionEvidence.SameNameFolder : ExtractionEvidence.Partial;
        return new ExtractedArchive(group.Parts, sameName, nameEvidence, 0, 0, folderBytes);
    }

    private readonly record struct ZipEntryInfo(string RelativePath, long Length, DateTimeOffset LastWriteTime);

    /// <summary>读出 zip 里的文件（不含文件夹条目）；读不了（损坏、不是 zip、分卷）时返回 null。</summary>
    private static List<ZipEntryInfo>? ReadZipEntries(string path)
    {
        try
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Read, LegacyNameEncoding());
            var entries = new List<ZipEntryInfo>();
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('/', '\\');
                if (name.EndsWith('\\'))
                {
                    continue; // 文件夹条目
                }

                // 带 .. 或盘符的条目名不安全，也不可能对应到解压出来的文件，整个压缩包都不认。
                if (System.IO.Path.IsPathRooted(name) || name.Split('\\').Any(s => s is ".."))
                {
                    return null;
                }

                entries.Add(new ZipEntryInfo(name.TrimStart('\\'), entry.Length, entry.LastWriteTime));
            }

            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static (int Count, long Bytes) CountMatches(List<ZipEntryInfo> entries, string root, string? stripPrefix, CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.RelativePath;
            if (stripPrefix is not null)
            {
                if (!relative.StartsWith(stripPrefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relative = relative[(stripPrefix.Length + 1)..];
            }

            var file = new FileInfo(System.IO.Path.Join(root, relative));
            if (file.Exists && file.Length == entry.Length && SameTime(file.LastWriteTime, entry.LastWriteTime))
            {
                count++;
                bytes += entry.Length;
            }
        }

        return (count, bytes);
    }

    /// <summary>
    /// 解压软件会把文件的修改时间设成压缩包里记录的时间。zip 只精确到 2 秒，而且不记时区，
    /// 所以相差 2 秒以内、或者正好差整数个小时（时区不同）都算一致。
    /// 光看大小不够：没打上的补丁包，里面的文件可能和原来的文件一样大。
    /// </summary>
    internal static bool SameTime(DateTime fileLocal, DateTimeOffset entry)
    {
        var difference = Math.Abs((fileLocal - entry.LocalDateTime).TotalSeconds);
        var hours = Math.Round(difference / 3600);
        return difference <= 2 || (hours <= 14 && Math.Abs(difference - (hours * 3600)) <= 2);
    }

    /// <summary>所有文件都在同一个顶层文件夹里时返回它的名字。</summary>
    private static string? SingleTopFolder(List<ZipEntryInfo> entries)
    {
        var tops = entries.Select(e => e.RelativePath.IndexOf('\\') is var i and > 0 ? e.RelativePath[..i] : null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tops is [{ } only] ? only : null;
    }

    private static long FolderSize(string path, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((file.Attributes & TreeScanner.CloudOnlyMask) == 0)
                {
                    total += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到的部分不算。
        }

        return total;
    }

    /// <summary>
    /// 没有标记 UTF-8 的 zip 条目名按系统的“非 Unicode 程序的语言”编码读（中文系统是 GBK），国内常见的压缩包都是这样打包的。
    /// </summary>
    private static Encoding LegacyNameEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding((int)GetACP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static List<string> NormalizeRoots(IReadOnlyList<string> roots)
    {
        var full = roots.Select(r => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(r)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)
            .ToList();

        var result = new List<string>();
        foreach (var root in full)
        {
            if (!result.Any(r => Rules.PathExpander.IsSameOrInside(root, r)))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private IEnumerable<ArchivePart> Enumerate(string root, ArchiveSearchOptions options, CancellationToken cancellationToken)
    {
        var enumerationOptions = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = true, BufferSize = 32 * 1024 };
        var guard = options.Guard;
        var skipPaths = options.SkipDirectories
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enumerable = new FileSystemEnumerable<ArchivePart>(root,
            (ref FileSystemEntry e) => new ArchivePart(e.ToFullPath(), e.Length, e.LastWriteTimeUtc),
            enumerationOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory
                && (e.Attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) == 0
                && LooksLikeArchive(e.FileName),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                if ((e.Attributes & (FileAttributes.ReparsePoint | TreeScanner.CloudOnlyMask)) != 0 || e.FileName is "$RECYCLE.BIN" or "System Volume Information")
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

    private static bool LooksLikeArchive(ReadOnlySpan<char> name)
    {
        var extension = System.IO.Path.GetExtension(name);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || NumberedVolume().IsMatch(name)
            || OldStyleVolume().IsMatch(name);
    }

    /// <summary>name.part1.rar、name.part01.rar ……</summary>
    [GeneratedRegex(@"^(.+)\.part(\d{1,4})\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarVolume();

    /// <summary>name.7z.001、name.zip.001、name.rar.001 ……</summary>
    [GeneratedRegex(@"^(.+)\.(7z|zip|rar)\.(\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedVolume();

    /// <summary>旧式分卷：name.r00 …… 属于 name.rar，name.z01 …… 属于 name.zip。</summary>
    [GeneratedRegex(@"^(.+)\.([rz])\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyleVolume();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();
}
