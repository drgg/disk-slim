using System.Runtime.InteropServices;

namespace DiskSlim.Core.Platform;

public readonly record struct RecycleBinInfo(long Bytes, long ItemCount);

/// <summary>读取回收站信息（只查询，不清空）。</summary>
public static partial class RecycleBin
{
    /// <summary>查询指定磁盘上回收站的大小，失败时返回 null。</summary>
    public static RecycleBinInfo? Query(string driveRoot)
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        var hr = SHQueryRecycleBin(driveRoot, ref info);
        return hr == 0 ? new RecycleBinInfo(info.Bytes, info.ItemCount) : null;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);

    /// <summary>SHQUERYRBINFO（64 位布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long Bytes;
        public long ItemCount;
    }
}
