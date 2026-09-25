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
public sealed class RuleMatcher(string driveRoot)
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

            foreach (var path in PathExpander.Expand(pattern))
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
}
