using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiskSlim.Core.Platform;

/// <summary>
/// 找到软件实际装在哪里：很多软件装在 D 盘、E 盘这类自选位置，只按 %LOCALAPPDATA% 这样的环境变量是找不到的。只读取注册表和配置文件。
/// </summary>
public static partial class InstalledApps
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>本机所有就绪的本地硬盘（不含光驱、网络盘和 U 盘）的根目录，例如 <c>D:\</c>。</summary>
    public static IReadOnlyList<string> FixedDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到的盘跳过。
            }
        }

        return roots;
    }

    /// <summary>
    /// 卸载信息里登记的、直接包含 <paramref name="exeName"/> 的安装目录。
    /// 同时看 InstallLocation 和 DisplayIcon 所在的目录：有的软件只写了其中一个，写的还不一定是 exe 所在的那一层。
    /// </summary>
    public static IReadOnlyList<string> DirectoriesContaining(string exeName)
    {
        var found = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                foreach (var candidate in UninstallCandidates(hive, keyPath))
                {
                    if (File.Exists(Path.Join(candidate, exeName)))
                    {
                        found.Add(Path.TrimEndingDirectorySeparator(candidate));
                    }
                }
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Steam 的全部游戏库目录（Steam 安装目录和在其他盘上添加的 SteamLibrary）。</summary>
    public static IReadOnlyList<string> SteamLibraries()
    {
        string? steamPath;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(steamPath) || !Path.IsPathFullyQualified(steamPath))
        {
            return [];
        }

        // 注册表里的 SteamPath 是全小写、用 / 分隔的，libraryfolders.vdf 里的写法才是正常大小写，所以先用它。
        steamPath = Path.GetFullPath(steamPath);
        var libraries = new List<string>();
        try
        {
            var vdf = Path.Join(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到配置时至少还有 Steam 安装目录。
        }

        libraries.Add(steamPath);

        return libraries
            .Where(Directory.Exists)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>从 libraryfolders.vdf 里取出每个游戏库的 "path"（反斜杠在文件里写成了 \\）。</summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdf) => LibraryPath().Matches(vdf)
        .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
        .Where(Path.IsPathFullyQualified)
        .Select(Path.GetFullPath);

    private static List<string> UninstallCandidates(RegistryKey hive, string keyPath)
    {
        var candidates = new List<string>();
        try
        {
            using var uninstall = hive.OpenSubKey(keyPath);
            if (uninstall is null)
            {
                return candidates;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var app = uninstall.OpenSubKey(name);
                if (app is null)
                {
                    continue;
                }

                if (CleanPath(app.GetValue("InstallLocation") as string) is { } location)
                {
                    candidates.Add(location);
                }

                // DisplayIcon 形如 "D:\Quark\quark.exe,8"：去掉图标序号和引号，取所在目录。
                if (CleanPath((app.GetValue("DisplayIcon") as string)?.Split(',')[0]) is { } icon && Path.GetDirectoryName(icon) is { } iconDirectory)
                {
                    candidates.Add(iconDirectory);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读不到的注册表项跳过。
        }

        return candidates;
    }

    private static string? CleanPath(string? value)
    {
        var path = value?.Trim().Trim('"').Trim();
        return !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            ? Path.GetFullPath(path)
            : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();
}
