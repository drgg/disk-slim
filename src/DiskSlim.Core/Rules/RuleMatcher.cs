using System.Diagnostics;

namespace DiskSlim.Core.Rules;

public sealed record ClaimedPath(string Path, bool IsFile);

/// <summary>规则命中的一个实际存在的路径。</summary>
/// <param name="Exclusions">位于该目录内、已经归属前面规则的路径（只列最外层）。统计和清理时都要排除。</param>
public sealed record MatchedTarget(string Path, bool IsFile, IReadOnlyList<ClaimedPath> Exclusions);

/// <summary>
/// 按顺序把规则的路径模板展开成实际路径，并保证同一个路径只归属第一条命中它的规则。
/// </summary>
/// <remarks>
/// 统计（<see cref="RuleEvaluator"/>）和清理（Cleaning）都通过它确定路径归属，
/// 所以两边对“哪些文件属于哪条规则”的判断总是一致的。规则必须按规则文件中的顺序依次传入。
/// </remarks>
/// <param name="tokens">解析 <c>&lt;drives&gt;</c> 这类路径前缀；为 null 时按这台电脑的实际情况解析。</param>
public sealed class RuleMatcher(string driveRoot, IPathTokenSource? tokens = null)
{
    private readonly List<ClaimedPath> _claims = [];

    public IReadOnlyList<MatchedTarget> Match(CleanupRule rule)
    {
        var targets = new List<MatchedTarget>();
        foreach (var pattern in rule.Paths)
        {
            if (PathExpander.IsSpecial(pattern))
            {
                continue;
            }

            var paths = PathExpander.Expand(pattern, tokens);
            foreach (var path in rule.OldVersionsOnly ? OldVersions(paths) : paths)
            {
                if (!PathExpander.IsSameOrInside(path, driveRoot) || _claims.Any(c => PathExpander.IsSameOrInside(path, c.Path)))
                {
                    continue;
                }

                var isFile = File.Exists(path);
                var exclusions = isFile ? [] : OutermostInside(_claims, path);
                _claims.Add(new ClaimedPath(path, isFile));
                targets.Add(new MatchedTarget(path, isFile, exclusions));
            }
        }

        return targets;
    }

    /// <summary>找出位于 <paramref name="directory"/> 内的路径，只保留最外层（内层的已经包含在外层里）。</summary>
    internal static List<ClaimedPath> OutermostInside(IEnumerable<ClaimedPath> paths, string directory)
    {
        var inside = paths
            .Where(p => PathExpander.IsSameOrInside(p.Path, directory) && !PathExpander.IsSameOrInside(directory, p.Path))
            .ToList();
        return inside
            .Where(p => !inside.Any(o => !o.IsFile && !ReferenceEquals(o, p) && PathExpander.IsSameOrInside(p.Path, o.Path)))
            .ToList();
    }

    /// <summary>
    /// 从按版本号命名的文件夹里挑出旧版本：同一个上级目录里，最新的版本和上级目录中程序（.exe）自己的版本都保留。
    /// </summary>
    /// <remarks>
    /// 例如 D:\Quark 里有 6.9.2.881、6.9.5.892、7.1.7.975 三个版本，quark.exe 是 6.9.2.881（新版本的 new_quark.exe 还没换上去），
    /// 这时 6.9.2.881 正在用、7.1.7.975 是下次要用的，只有 6.9.5.892 是旧版本。名字不是版本号的文件夹一律不动。
    /// </remarks>
    internal static IEnumerable<string> OldVersions(IEnumerable<string> paths)
    {
        foreach (var folder in paths.Where(Directory.Exists).GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var versioned = folder
                .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
                .Where(v => v.Version is not null)
                .ToList();
            if (versioned.Count < 2)
            {
                continue;
            }

            var keep = new HashSet<Version> { versioned.Max(v => v.Version)! };
            keep.UnionWith(ProgramVersions(folder.Key));
            foreach (var (path, version) in versioned)
            {
                if (!keep.Contains(version!))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>“7.1.7.975” 这样至少三段的版本号，统一补成四段；不是版本号时返回 null。</summary>
    internal static Version? ParseVersion(string? text) =>
        text is not null && text.Count(c => c == '.') >= 2 && Version.TryParse(text, out var version) ? Normalize(version) : null;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>目录里各个 .exe 的文件版本和产品版本。</summary>
    private static HashSet<Version> ProgramVersions(string directory)
    {
        var versions = new HashSet<Version>();
        try
        {
            foreach (var exe in Directory.EnumerateFiles(directory, "*.exe"))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                versions.Add(new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart));
                if (ParseVersion(info.ProductVersion?.Split(' ')[0]) is { } product)
                {
                    versions.Add(product);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到程序的版本时，只保留最新的版本会有风险：这个目录里的版本全部保留。
            return AllVersions(directory);
        }

        return versions;
    }

    private static HashSet<Version> AllVersions(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).Select(d => ParseVersion(Path.GetFileName(d))).OfType<Version>().ToHashSet();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连目录都读不了，也就找不到要处理的旧版本。
            return [];
        }
    }
}
