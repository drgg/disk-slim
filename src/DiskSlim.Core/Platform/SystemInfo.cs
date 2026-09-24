using System.Diagnostics;
using System.Security.Principal;

namespace DiskSlim.Core.Platform;

public static class SystemInfo
{
    public static string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>当前运行中的进程名（不含 .exe，不区分大小写）。</summary>
    public static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出。
                }
            }
        }

        return names;
    }
}
