using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSlim.Core.Platform;

/// <summary>
/// 文件在磁盘上的身份（卷序列号 + 文件索引）。同一个文件的多个硬链接身份相同：
/// 它们不是重复文件，删掉其中一个不会释放任何空间。
/// </summary>
public readonly partial record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount)
{
    /// <summary>读取已打开文件的身份，失败时返回 null。</summary>
    public static FileIdentity? Of(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    /// <summary>同一个物理文件（忽略链接数）。</summary>
    public bool IsSameFile(FileIdentity other) => VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>BY_HANDLE_FILE_INFORMATION。FILETIME 由两个 DWORD 组成、按 4 字节对齐，所以这里拆成 uint，不能用 long。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
