using DiskSlim.Core.Rules;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.Reporting;

public sealed record ScanReport
{
    public required string ToolVersion { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required bool IsElevated { get; init; }

    public required DriveSummary Drive { get; init; }

    /// <summary>是否执行了规则检查。</summary>
    public bool RulesChecked { get; init; }

    public int RuleCount { get; init; }

    /// <summary>命中且有内容的规则。</summary>
    public IReadOnlyList<RuleResult> Rules { get; init; } = [];

    public SpaceMap? SpaceMap { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record DriveSummary(string Root, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedRatio => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public sealed record SpaceMap
{
    public required string Root { get; init; }

    public required IReadOnlyList<FolderView> Folders { get; init; }

    public required IReadOnlyList<FileEntry> LargestFiles { get; init; }

    public required ScanStats Stats { get; init; }

    public required double ElapsedSeconds { get; init; }

    /// <summary>无法访问的目录（最多记录若干条）。</summary>
    public IReadOnlyList<ScanError> Errors { get; init; } = [];
}

/// <summary>某个目录下占用最大的子项。</summary>
public sealed record FolderView(string Title, string Path, long TotalBytes, IReadOnlyList<FolderItem> Items);

/// <param name="IsOwnFiles">为 true 时表示“直接位于该目录中的文件”这一合计项，而不是子目录。</param>
/// <param name="Incomplete">其中有无法读取的目录，实际大小可能更大。</param>
public sealed record FolderItem(string Name, string Path, long Bytes, long FileCount, bool IsOwnFiles, bool Incomplete);
