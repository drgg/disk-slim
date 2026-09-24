using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Visualization;

/// <summary>
/// 把规则检查的结果对应到扫描树上：每个目录里有多少“可清理”的空间、它本身是不是某条规则命中的目标。
/// 方块图用它给目录着色，并在上层目录上标出里面藏着多少可清理的内容。
/// </summary>
public sealed class CleanableIndex
{
    private readonly Dictionary<DirNode, long> _cleanableBytes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, RiskLevel> _targets = new(StringComparer.OrdinalIgnoreCase);

    public static readonly CleanableIndex Empty = new();

    private CleanableIndex()
    {
    }

    /// <summary>
    /// “可清理”指“可放心清理”和“需要确认”两类规则的可释放空间；“仅供参考”的不算。
    /// </summary>
    public static CleanableIndex Build(DirNode root, IEnumerable<RuleResult> results)
    {
        var index = new CleanableIndex();
        var recycleBinPath = Path.Join(Path.GetPathRoot(root.FullPath), "$Recycle.Bin");
        foreach (var result in results)
        {
            foreach (var match in result.Matches)
            {
                // 回收站的结果没有真实路径（只有“回收站（C:\）”这样的名称），对应到磁盘上的 $Recycle.Bin。
                var path = Path.IsPathFullyQualified(match.Path) ? match.Path
                    : result.Rule.Paths.Any(PathExpander.IsRecycleBin) ? recycleBinPath
                    : null;
                if (path is null)
                {
                    continue;
                }

                index._targets.TryAdd(path, result.Rule.Risk);
                if (result.Rule.Risk == RiskLevel.High || match.ReclaimableBytes <= 0)
                {
                    continue;
                }

                // 文件目标（比如下载文件夹里的安装包）记到它所在的目录上。
                var node = root.FindDescendant(path)
                    ?? (Path.GetDirectoryName(path) is { } parent ? root.FindDescendant(parent) : null);
                for (var n = node; n is not null; n = n.Parent)
                {
                    index._cleanableBytes[n] = index._cleanableBytes.GetValueOrDefault(n) + match.ReclaimableBytes;
                }
            }
        }

        return index;
    }

    /// <summary>该目录（含子目录）中可清理的字节数。</summary>
    public long CleanableBytes(DirNode node) => _cleanableBytes.GetValueOrDefault(node);

    /// <summary>该路径本身是某条规则命中的目标时，返回那条规则的风险等级。</summary>
    public RiskLevel? TargetRisk(string path) => _targets.TryGetValue(path, out var risk) ? risk : null;
}
