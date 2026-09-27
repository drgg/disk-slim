using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Cleaning;

/// <summary>
/// 最后一道防线：无论规则文件怎么写（包括用户自定义规则），都不允许清理这些位置。
/// </summary>
/// <remarks>
/// 两类保护：
/// <list type="bullet">
/// <item><b>关键目录</b>（用户目录、文档、桌面、AppData 根目录、Windows 目录……）：不能作为清理目标，
/// 也不能是清理目标的上级；但它们<i>里面</i>的具体子目录可以，例如 AppData\Local\Temp。</item>
/// <item><b>禁区</b>（System32、WinSxS、Program Files、凭据目录……）：里面的任何东西都不能清理。</item>
/// </list>
/// 磁盘根目录总是受保护。
/// </remarks>
public sealed class SafetyGuard
{
    private readonly IReadOnlyList<string> _criticalFolders;
    private readonly IReadOnlyList<string> _forbiddenZones;

    public SafetyGuard(IEnumerable<string> criticalFolders, IEnumerable<string> forbiddenZones)
    {
        _criticalFolders = Normalize(criticalFolders);
        _forbiddenZones = Normalize(forbiddenZones);
    }

    /// <summary>按当前用户和系统的实际路径创建。</summary>
    public static SafetyGuard ForCurrentUser()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var windows = Folder(Environment.SpecialFolder.Windows);
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Folder(Environment.SpecialFolder.ApplicationData);

        var critical = new List<string>
        {
            windows,
            Path.GetDirectoryName(profile) ?? "",             // C:\Users
            Folder(Environment.SpecialFolder.CommonDocuments), // C:\Users\Public\Documents
            profile,
            local,
            roaming,
            Path.Combine(profile, "AppData"),
            Path.Combine(profile, "AppData", "LocalLow"),
            Folder(Environment.SpecialFolder.MyDocuments),
            Folder(Environment.SpecialFolder.Desktop),
            Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.MyMusic),
            Folder(Environment.SpecialFolder.MyVideos),
            Folder(Environment.SpecialFolder.Favorites),
            Path.Combine(profile, "Downloads"),
            Folder(Environment.SpecialFolder.CommonApplicationData), // C:\ProgramData
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        };

        var forbidden = new List<string>
        {
            Folder(Environment.SpecialFolder.System),
            Folder(Environment.SpecialFolder.SystemX86),
            Path.Combine(windows, "WinSxS"),
            Path.Combine(windows, "Installer"),
            Path.Combine(windows, "servicing"),
            Path.Combine(windows, "Boot"),
            Path.Combine(windows, "Fonts"),
            Path.Combine(windows, "assembly"),
            Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(profile, ".ssh"),
            Path.Combine(profile, ".gnupg"),
            Path.Combine(local, "Microsoft", "Credentials"),
            Path.Combine(roaming, "Microsoft", "Credentials"),
            Path.Combine(roaming, "Microsoft", "Crypto"),
            Path.Combine(roaming, "Microsoft", "Protect"),
            Path.Combine(roaming, "Microsoft", "SystemCertificates"),

            // DiskSlim 自己的操作日志和隔离区：只能通过隔离区的恢复 / 彻底删除来处理。
            Path.Combine(local, "DiskSlim"),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    forbidden.Add(Quarantine.QuarantineStore.DefaultRoot(drive.RootDirectory.FullName));
                    forbidden.Add(Quarantine.QuarantineStore.LegacyRootFor(drive.RootDirectory.FullName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到的盘不影响其他保护。
            }
        }

        return new SafetyGuard(critical, forbidden);
    }

    /// <summary>这个目录是否正好是一个禁区（遍历时在这里停下，里面的东西都不用看）。</summary>
    public bool IsForbiddenZone(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return _forbiddenZones.Any(z => string.Equals(z, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>检查一个清理目标，允许时返回 null，否则返回原因。</summary>
    public string? Check(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase))
        {
            return $"{full} 是磁盘根目录";
        }

        foreach (var zone in _forbiddenZones)
        {
            if (PathExpander.IsSameOrInside(full, zone))
            {
                return $"{full} 位于受保护的目录 {zone} 中";
            }

            if (PathExpander.IsSameOrInside(zone, full))
            {
                return $"{full} 包含受保护的目录 {zone}";
            }
        }

        foreach (var folder in _criticalFolders)
        {
            if (PathExpander.IsSameOrInside(folder, full))
            {
                return string.Equals(folder, full, StringComparison.OrdinalIgnoreCase)
                    ? $"{full} 是系统或用户的关键目录，不能整体清理"
                    : $"{full} 包含关键目录 {folder}";
            }
        }

        return null;
    }

    private static List<string> Normalize(IEnumerable<string> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
        .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
