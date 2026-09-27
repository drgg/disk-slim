using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiskSlim.Core.Platform;

/// <summary>系统级清理任务运行前要知道的系统状态。只读取，不修改。</summary>
public static partial class SystemState
{
    private const uint WofProviderFile = 2;

    /// <summary>
    /// 组件服务（CBS）有等重启才能完成的操作。这时 DISM 的组件清理会失败并返回 0x800F0806，
    /// 所以先检查，免得白白弹出 UAC、等上几分钟再失败。
    /// </summary>
    public static bool ComponentServicingRebootPending()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>物理内存大小，用来估算休眠文件改成精简模式后的大小；读不到时返回 0。</summary>
    public static long PhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    /// <summary>
    /// 系统文件是否已经用 CompactOS 压缩：抽查几个一定会被压缩的系统程序，看它们是否由 WOF（Windows 覆盖文件筛选器）按压缩文件存放。
    /// </summary>
    public static bool CompactOsEnabled()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] samples = [Path.Combine(windows, "explorer.exe"), Path.Combine(Environment.SystemDirectory, "cmd.exe")];
        try
        {
            return samples.Any(file => File.Exists(file)
                && WofIsExternalFile(file, out var isExternal, out var provider, 0, 0) >= 0
                && isExternal && provider == WofProviderFile);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("wofutil.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WofIsExternalFile(string filePath, [MarshalAs(UnmanagedType.Bool)] out bool isExternalFile, out uint provider, nint externalFileInfo, nint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>MEMORYSTATUSEX。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
