using System.Text.RegularExpressions;

namespace DiskSlim.Core.Rules;

/// <summary>把规则中的路径模板展开成实际存在的路径。</summary>
public static partial class PathExpander
{
    /// <summary>表示回收站的特殊路径。</summary>
    public const string RecycleBinToken = "::RecycleBin";

    private static readonly EnumerationOptions WildcardOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
    };

    /// <summary>表示 DiskSlim 自己的隔离区的特殊路径。</summary>
    public const string QuarantineToken = "::Quarantine";

    public static bool IsRecycleBin(string pattern) =>
        string.Equals(pattern.Trim(), RecycleBinToken, StringComparison.OrdinalIgnoreCase);

    public static bool IsQuarantine(string pattern) =>
        string.Equals(pattern.Trim(), QuarantineToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否是 ::RecycleBin、::Quarantine 这类特殊路径（不是真实路径，不参与展开）。</summary>
    public static bool IsSpecial(string pattern) => IsRecycleBin(pattern) || IsQuarantine(pattern);

    /// <summary>
    /// 展开环境变量、<c>&lt;drives&gt;</c> 这类路径前缀（见 <see cref="PathTokens"/>）和通配符，返回实际存在的文件或目录（按路径排序）。
    /// 环境变量未定义、前缀不认识、路径不是绝对路径或者不存在时返回空列表。
    /// </summary>
    /// <param name="tokens">解析路径前缀；为 null 时按这台电脑的实际情况解析。</param>
    public static IReadOnlyList<string> Expand(string pattern, IPathTokenSource? tokens = null)
    {
        if (!PathTokens.TrySplit(pattern, out var token, out var rest))
        {
            var expanded = Environment.ExpandEnvironmentVariables(pattern.Trim()).Replace('/', '\\');
            return UnresolvedVariable().IsMatch(expanded) ? [] : Walk(expanded);
        }

        if (!PathTokens.IsKnown(token))
        {
            return [];
        }

        // 前缀代表的目录是真实路径，不再展开其中的环境变量；只展开后半部分。
        var suffix = Environment.ExpandEnvironmentVariables(rest).Replace('/', '\\');
        if (UnresolvedVariable().IsMatch(suffix))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var basePath in (tokens ?? SystemPathTokens.Instance).Resolve(token))
        {
            results.AddRange(Walk(suffix.Length == 0 ? basePath : Path.Join(basePath, suffix)));
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>逐级展开通配符，只返回实际存在的路径。</summary>
    private static List<string> Walk(string path)
    {
        var expanded = path.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(expanded))
        {
            return [];
        }

        expanded = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(expanded)!;
        var segments = expanded[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Directory.Exists(root) ? [root] : [];
        }

        List<string> current = [root];
        for (var i = 0; i < segments.Length && current.Count > 0; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            var next = new List<string>();
            foreach (var basePath in current)
            {
                if (segment.AsSpan().IndexOfAny('*', '?') >= 0)
                {
                    next.AddRange(Match(basePath, segment, includeFiles: isLast));
                }
                else
                {
                    var candidate = Path.Join(basePath, segment);
                    if (isLast ? Path.Exists(candidate) : Directory.Exists(candidate))
                    {
                        next.Add(candidate);
                    }
                }
            }

            current = next;
        }

        current.Sort(StringComparer.OrdinalIgnoreCase);
        return current;
    }

    /// <summary>判断 <paramref name="path"/> 是否等于 <paramref name="ancestor"/> 或位于其中。</summary>
    public static bool IsSameOrInside(string path, string ancestor)
    {
        var a = ancestor.TrimEnd('\\');
        var p = path.TrimEnd('\\');
        return p.Equals(a, StringComparison.OrdinalIgnoreCase)
            || (p.Length > a.Length && p[a.Length] == '\\' && p.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Match(string basePath, string pattern, bool includeFiles)
    {
        List<FileSystemInfo> entries;
        try
        {
            var directory = new DirectoryInfo(basePath);
            entries = includeFiles
                ? directory.EnumerateFileSystemInfos(pattern, WildcardOptions).ToList()
                : [.. directory.EnumerateDirectories(pattern, WildcardOptions)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // 通配符不展开到目录联接/符号链接里，避免同一份数据通过不同路径被重复统计。
        return entries.Where(e => !IsLink(e)).Select(e => e.FullName);
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [GeneratedRegex(@"%[^%\\]+%")]
    private static partial Regex UnresolvedVariable();
}
