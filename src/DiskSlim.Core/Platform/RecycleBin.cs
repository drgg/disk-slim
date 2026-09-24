using System.Runtime.InteropServices;

namespace DiskSlim.Core.Platform;

public readonly record struct RecycleBinInfo(long Bytes, long ItemCount);

/// <summary>回收站：查询大小、把文件移进回收站、清空回收站。</summary>
public static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    // 文件无法放进回收站（比如超过回收站容量）时弹窗确认，而不是悄悄地永久删除。
    private const ushort FofWantNukeWarning = 0x4000;

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    /// <summary>查询指定磁盘上回收站的大小，失败时返回 null。</summary>
    public static RecycleBinInfo? Query(string driveRoot)
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        var hr = SHQueryRecycleBin(driveRoot, ref info);
        return hr == 0 ? new RecycleBinInfo(info.Bytes, info.ItemCount) : null;
    }

    /// <summary>把一个文件移到回收站。</summary>
    /// <exception cref="IOException">移动失败，文件仍在原处。</exception>
    public static void MoveToRecycleBin(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length >= 260)
        {
            throw new IOException("路径太长，无法移到回收站");
        }

        // pFrom 必须以两个 \0 结尾。
        var from = Marshal.StringToHGlobalUni(fullPath + '\0');
        try
        {
            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
            };
            var result = SHFileOperation(ref op);
            if (result != 0 || op.AnyOperationsAborted != 0)
            {
                throw new IOException($"移到回收站失败（错误码 0x{result:X}）");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }

        if (File.Exists(fullPath))
        {
            throw new IOException("移到回收站失败：文件仍在原处");
        }
    }

    /// <summary>清空指定磁盘上的回收站（不弹确认框）。回收站本来就是空的也算成功。</summary>
    /// <exception cref="IOException">清空失败。</exception>
    public static void Empty(string driveRoot)
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, driveRoot, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        if (hr != 0 && hr != EUnexpected)
        {
            throw new IOException($"清空回收站失败（错误码 0x{hr:X8}）");
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>SHQUERYRBINFO（64 位布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShQueryRbInfo
    {
        public int Size;
        public long Bytes;
        public long ItemCount;
    }

    /// <summary>SHFILEOPSTRUCTW（64 位布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }
}
