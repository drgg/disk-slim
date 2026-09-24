using System.Collections.ObjectModel;
using DiskSlim.Core.Formatting;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Rules;

namespace DiskSlim.Desktop.ViewModels;

/// <summary>“可清理项目”列表中的一条。</summary>
public sealed class RuleItemViewModel : ObservableObject
{
    private bool _isSelected;

    public RuleItemViewModel(RuleResult result)
    {
        Result = result;

        // 和命令行一致：“可放心清理”默认选中，“需要确认”默认不选，“仅供参考”不能选。
        _isSelected = IsSelectable && result.Rule.Risk == RiskLevel.Low;
    }

    public event EventHandler? SelectionChanged;

    public RuleResult Result { get; }

    public CleanupRule Rule => Result.Rule;

    public string Id => Rule.Id;

    public string Name => Rule.Name;

    public string Advice => Rule.Advice;

    public RiskLevel Risk => Rule.Risk;

    /// <summary>界面上显示的主要大小：可释放空间（“仅供参考”的项目显示总大小）。</summary>
    public long CountedBytes => DisplayText.CountedBytes(Result);

    public string SizeText => ByteSize.Format(CountedBytes);

    public string DetailText
    {
        get
        {
            var text = $"共 {ByteSize.Format(Result.TotalBytes)} · {Result.FileCount:N0} 个文件";
            return Rule.MinAgeDays is > 0 and var days && Result.ReclaimableBytes < Result.TotalBytes
                ? text + $" · 只计入 {days} 天前的文件"
                : text;
        }
    }

    public bool IsSelectable => DisplayText.WhyNotCleanable(Rule) is null;

    public string ActionText => IsSelectable ? DisplayText.ActionName(Rule.Action) : DisplayText.WhyNotCleanable(Rule)!;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value && IsSelectable))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? Warning
    {
        get
        {
            var warnings = new List<string>();
            if (Result.RunningProcesses.Count > 0)
            {
                warnings.Add($"{string.Join("、", Result.RunningProcesses)} 正在运行，清理时会跳过这一项，请先关闭");
            }

            if (Result.InaccessibleDirectories > 0)
            {
                warnings.Add($"有 {Result.InaccessibleDirectories:N0} 个文件夹无权限读取，以管理员身份运行可以统计和清理更多");
            }

            return warnings.Count == 0 ? null : string.Join("；", warnings);
        }
    }

    public bool HasWarning => Warning is not null;
}

/// <summary>按风险等级分组。</summary>
public sealed class RuleGroupViewModel(RiskLevel risk, IReadOnlyList<RuleItemViewModel> items) : ObservableObject
{
    public RiskLevel Risk { get; } = risk;

    public string Title => DisplayText.RiskTitle(Risk);

    public string Description => Risk switch
    {
        RiskLevel.Low => "缓存和临时文件，删除后会自动重建",
        RiskLevel.Medium => "删除后需要重新下载，或需要你判断是否还有用",
        _ => "用户数据或系统文件，只给出建议，工具不会清理",
    };

    public ObservableCollection<RuleItemViewModel> Items { get; } = new(items);

    public string TotalText => (Risk == RiskLevel.High ? "合计 " : "可释放 ") + ByteSize.Format(Items.Sum(i => i.CountedBytes));
}

public sealed record DriveOption(string Root, string Label, bool IsSystem)
{
    public static IReadOnlyList<DriveOption> Available(string systemRoot)
    {
        var options = new List<DriveOption>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                var isSystem = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase);
                options.Add(new DriveOption(drive.RootDirectory.FullName, $"{drive.RootDirectory.FullName}  {name} · 剩余 {ByteSize.Format(drive.TotalFreeSpace)}", isSystem));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到的盘就不列出。
            }
        }

        return options;
    }
}

/// <summary>空间分布右侧列表中的一项。</summary>
public sealed record FolderItemViewModel(string Name, string Path, long Bytes, double Ratio, long CleanableBytes, Core.Scanning.DirNode? Node)
{
    public string SizeText => ByteSize.Format(Bytes);

    public string RatioText => $"{Ratio * 100:0.#}%";

    public bool IsDirectory => Node is not null;

    public string? CleanableText => CleanableBytes > 0 ? $"可清理 {ByteSize.Format(CleanableBytes)}" : null;

    public bool HasCleanable => CleanableBytes > 0;
}

public sealed record BreadcrumbItem(string Name, Core.Scanning.DirNode Node, bool IsLast);

public sealed record LargeFileViewModel(string Path, long Bytes, DateTimeOffset LastWriteTimeUtc)
{
    public string SizeText => ByteSize.Format(Bytes);

    public string DateText => LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd");

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}
