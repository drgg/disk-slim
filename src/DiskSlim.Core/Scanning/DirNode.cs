namespace DiskSlim.Core.Scanning;

/// <summary>扫描结果树中的一个目录。</summary>
/// <remarks>
/// 扫描过程中每个节点只会被一个工作线程写入；扫描结束后自底向上汇总 Total* 属性，之后只读。
/// </remarks>
public sealed class DirNode
{
    private static readonly IReadOnlyList<DirNode> NoChildren = [];
    private List<DirNode>? _children;

    internal DirNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>根节点是完整路径，其余节点是目录名。</summary>
    public string Name { get; }

    public DirNode? Parent { get; }

    public IReadOnlyList<DirNode> Children => _children ?? NoChildren;

    /// <summary>直接位于本目录中的文件大小之和（不含子目录）。</summary>
    public long OwnBytes { get; internal set; }

    /// <summary>直接位于本目录中、满足“可释放”时间条件的文件大小之和。</summary>
    public long OwnReclaimableBytes { get; internal set; }

    public int OwnFileCount { get; internal set; }

    public long TotalBytes { get; internal set; }

    public long TotalReclaimableBytes { get; internal set; }

    public long TotalFileCount { get; internal set; }

    /// <summary>读取本目录时遇到的 Win32 错误码，0 表示正常。</summary>
    public int ErrorCode { get; internal set; }

    /// <summary>本目录及所有子目录中无法完整读取的目录数。大于 0 时，统计值可能偏小。</summary>
    public long TotalInaccessibleCount { get; internal set; }

    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return Name;
            }

            var names = new Stack<string>();
            DirNode? node = this;
            while (node.Parent is not null)
            {
                names.Push(node.Name);
                node = node.Parent;
            }

            return Path.Join([node.Name, .. names]);
        }
    }

    /// <summary>按完整路径查找本节点或其子孙节点，找不到时返回 null。</summary>
    public DirNode? FindDescendant(string fullPath)
    {
        var relative = Path.GetRelativePath(FullPath, fullPath);
        if (relative == ".")
        {
            return this;
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal))
        {
            return null;
        }

        var node = this;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            DirNode? next = null;
            foreach (var child in node.Children)
            {
                if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    internal void SetChildren(List<DirNode>? children) => _children = children;
}
