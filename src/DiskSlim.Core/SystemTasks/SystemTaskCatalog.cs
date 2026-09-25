using DiskSlim.Core.Scanning;

namespace DiskSlim.Core.SystemTasks;

/// <summary>一项系统级清理任务。任务的具体做法写死在 <see cref="SystemTaskRunner"/> 里，不能通过规则文件或参数修改。</summary>
/// <param name="RelatedRuleId">对应的清理规则（扫描报告里据此提示可以用这项任务清理）。</param>
public sealed record SystemTask(string Id, string Name, string Description, string Warning, string? UndoHint, string? RelatedRuleId);

/// <param name="NotNeeded">已经不需要运行（例如休眠已经关闭）。</param>
public sealed record SystemTaskEstimate(long? Bytes, string Note, bool NotNeeded = false);

/// <summary>系统级清理任务的固定清单。管理员权限的助手进程只接受这里列出的任务。</summary>
public static class SystemTaskCatalog
{
    public const string ComponentStore = "component-store";
    public const string WindowsUpdateCache = "windows-update-cache";
    public const string DeliveryOptimization = "delivery-optimization";
    public const string HibernationOff = "hibernation-off";

    public static IReadOnlyList<SystemTask> All { get; } =
    [
        new(ComponentStore, "清理 Windows 组件存储（WinSxS）",
            "用 DISM 删除已被新版本替代的旧系统组件（/StartComponentCleanup）。不使用 /ResetBase，已安装的更新仍然可以卸载。",
            "耗时较长（几分钟到半小时），期间不要关机，也不要同时安装更新。",
            null, null),
        new(WindowsUpdateCache, "清理 Windows 更新下载缓存",
            "暂停 Windows 更新服务和后台传输服务，删除已经下载过的更新安装包，再把服务恢复成原来的状态。以后需要时 Windows 会重新下载。",
            "系统正在下载或安装更新时不要运行。",
            null, "windows-update-cache"),
        new(DeliveryOptimization, "清理传递优化缓存",
            "用系统自带的 Delete-DeliveryOptimizationCache 删除为局域网内其他电脑分享更新而保留的缓存。",
            "不影响已经安装的更新。",
            null, "delivery-optimization"),
        new(HibernationOff, "关闭休眠（删除 hiberfil.sys）",
            "运行 powercfg /hibernate off，系统会删除休眠文件，释放和它一样大的空间。",
            "关闭后不能再“休眠”，“快速启动”也会一起关闭（开机可能稍慢一点）。",
            "想恢复：在管理员终端运行 powercfg /hibernate on", "hibernation-file"),
    ];

    public static SystemTask? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static SystemTask? ForRule(string ruleId) =>
        All.FirstOrDefault(t => string.Equals(t.RelatedRuleId, ruleId, StringComparison.OrdinalIgnoreCase));

    internal static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    internal static string UpdateDownloadDirectory => Path.Combine(WindowsDirectory, "SoftwareDistribution", "Download");

    internal static string DeliveryOptimizationCacheDirectory =>
        Path.Combine(WindowsDirectory, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache");

    internal static string HibernationFile => Path.Combine(Path.GetPathRoot(WindowsDirectory)!, "hiberfil.sys");

    /// <summary>估计这项任务能释放多少空间（只读）。</summary>
    public static SystemTaskEstimate Estimate(SystemTask task) => task.Id switch
    {
        ComponentStore => new(null, "由 DISM 在运行时计算；WinSxS 里大部分是硬链接，资源管理器显示的大小并不准"),
        WindowsUpdateCache => MeasureDirectory(UpdateDownloadDirectory),
        DeliveryOptimization => MeasureDirectory(DeliveryOptimizationCacheDirectory),
        HibernationOff => File.Exists(HibernationFile)
            ? new(new FileInfo(HibernationFile).Length, "")
            : new(0, "休眠已经是关闭的", NotNeeded: true),
        _ => new(null, ""),
    };

    private static SystemTaskEstimate MeasureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            // 没有权限读取上级目录时，Directory.Exists 也会返回 false。
            return Platform.SystemInfo.IsElevated() ? new(0, "没有找到这个目录") : new(null, "需要管理员权限才能统计");
        }

        var result = new TreeScanner().Scan(path);
        return result.Stats.InaccessibleDirectories > 0 && result.Root.TotalBytes == 0
            ? new(null, "需要管理员权限才能统计")
            : new(result.Root.TotalBytes, result.Stats.InaccessibleDirectories > 0 ? "部分目录需要管理员权限，实际可能更大" : "");
    }
}
