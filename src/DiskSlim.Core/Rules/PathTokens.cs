using DiskSlim.Core.Platform;

namespace DiskSlim.Core.Rules;

/// <summary>
/// 规则路径开头的 <c>&lt;名字&gt;</c> 前缀，代表那些位置不固定、用环境变量表示不了的目录：
/// <list type="bullet">
/// <item><c>&lt;drives&gt;</c>：每块本地硬盘的根目录，例如 <c>&lt;drives&gt;/BaiduNetdiskDownload</c> 能找到 D:\BaiduNetdiskDownload；</item>
/// <item><c>&lt;steam-libraries&gt;</c>：Steam 的每个游戏库（包括其他盘上的 SteamLibrary）；</item>
/// <item><c>&lt;app:quark.exe&gt;</c>：卸载信息里登记的、直接包含 quark.exe 的安装目录。</item>
/// </list>
/// Windows 路径里不能出现 &lt; 和 &gt;，所以这种写法不会和真实路径混淆。
/// </summary>
public interface IPathTokenSource
{
    /// <summary>返回前缀代表的目录（可能有多个，也可能一个都没有）。</summary>
    IReadOnlyList<string> Resolve(string token);
}

public static class PathTokens
{
    public const string Drives = "drives";
    public const string SteamLibraries = "steam-libraries";
    public const string AppPrefix = "app:";

    /// <summary>把 “&lt;drives&gt;/BaiduNetdiskDownload” 拆成前缀名 “drives” 和其余部分 “BaiduNetdiskDownload”；不是前缀写法时返回 false。</summary>
    public static bool TrySplit(string pattern, out string token, out string rest)
    {
        token = rest = "";
        var trimmed = pattern.Trim();
        var end = trimmed.IndexOf('>');
        if (!trimmed.StartsWith('<') || end < 0)
        {
            return false;
        }

        token = trimmed[1..end];
        rest = trimmed[(end + 1)..].TrimStart('/', '\\');
        return true;
    }

    /// <summary>规则文件里能用的前缀。</summary>
    public static bool IsKnown(string token) =>
        token is Drives or SteamLibraries
        || (token.StartsWith(AppPrefix, StringComparison.Ordinal) && ExeName(token) is not null);

    internal static string? ExeName(string token)
    {
        var name = token[AppPrefix.Length..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4
            && name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0
            ? name
            : null;
    }
}

/// <summary>按这台电脑的实际情况解析路径前缀（读取磁盘列表、注册表和 Steam 的配置）。</summary>
/// <remarks>
/// 找到的目录如果在安全防线的禁区里（例如装在 C:\Program Files (x86)\Steam 的 Steam），就不算：
/// 那里本来就不允许清理，报告里列出来只会让“可释放”的数字和实际清理对不上。
/// </remarks>
public sealed class SystemPathTokens : IPathTokenSource
{
    public static SystemPathTokens Instance { get; } = new();

    public IReadOnlyList<string> Resolve(string token)
    {
        IReadOnlyList<string> found = token switch
        {
            PathTokens.Drives => InstalledApps.FixedDriveRoots(),
            PathTokens.SteamLibraries => InstalledApps.SteamLibraries(),
            _ when token.StartsWith(PathTokens.AppPrefix, StringComparison.Ordinal) && PathTokens.ExeName(token) is { } exe => InstalledApps.DirectoriesContaining(exe),
            _ => [],
        };

        if (token == PathTokens.Drives || found.Count == 0)
        {
            return found;
        }

        var guard = Cleaning.SafetyGuard.ForCurrentUser();
        return found.Where(path => !guard.IsInForbiddenZone(path)).ToList();
    }
}
