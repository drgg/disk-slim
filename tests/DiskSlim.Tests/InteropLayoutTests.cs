using System.Runtime.InteropServices;
using DiskSlim.Core.Platform;

namespace DiskSlim.Tests;

/// <summary>
/// 校验与 Windows API 交互的结构体布局。布局错了，API 会读到错误的字段（比如错误的删除标志），
/// 所以这里按 Windows SDK 头文件中 64 位下的大小和偏移逐一核对。
/// </summary>
public sealed class InteropLayoutTests
{
    [Fact]
    public void Runs_as_64_bit_process()
    {
        Assert.Equal(8, IntPtr.Size);
    }

    [Fact]
    public void SHFILEOPSTRUCTW_matches_the_sdk_layout()
    {
        Assert.Equal(56, Marshal.SizeOf<RecycleBin.ShFileOpStruct>());
        Assert.Equal(0, Offset(nameof(RecycleBin.ShFileOpStruct.Hwnd)));
        Assert.Equal(8, Offset(nameof(RecycleBin.ShFileOpStruct.Func)));
        Assert.Equal(16, Offset(nameof(RecycleBin.ShFileOpStruct.From)));
        Assert.Equal(24, Offset(nameof(RecycleBin.ShFileOpStruct.To)));
        Assert.Equal(32, Offset(nameof(RecycleBin.ShFileOpStruct.Flags)));
        Assert.Equal(36, Offset(nameof(RecycleBin.ShFileOpStruct.AnyOperationsAborted)));
        Assert.Equal(40, Offset(nameof(RecycleBin.ShFileOpStruct.NameMappings)));
        Assert.Equal(48, Offset(nameof(RecycleBin.ShFileOpStruct.ProgressTitle)));

        static int Offset(string field) => (int)Marshal.OffsetOf<RecycleBin.ShFileOpStruct>(field);
    }

    [Fact]
    public void SHQUERYRBINFO_matches_the_sdk_layout()
    {
        Assert.Equal(24, Marshal.SizeOf<RecycleBin.ShQueryRbInfo>());
        Assert.Equal(8, (int)Marshal.OffsetOf<RecycleBin.ShQueryRbInfo>(nameof(RecycleBin.ShQueryRbInfo.Bytes)));
    }

    [Fact]
    public void FILE_RENAME_INFO_matches_the_sdk_layout()
    {
        Assert.Equal(0, Offset(nameof(HandleOperations.FileRenameInfoHeader.ReplaceIfExists)));
        Assert.Equal(8, Offset(nameof(HandleOperations.FileRenameInfoHeader.RootDirectory)));
        Assert.Equal(16, Offset(nameof(HandleOperations.FileRenameInfoHeader.FileNameLength)));
        Assert.Equal(20, HandleOperations.FileRenameInfoNameOffset);

        static int Offset(string field) => (int)Marshal.OffsetOf<HandleOperations.FileRenameInfoHeader>(field);
    }

    [Fact]
    public void Querying_the_recycle_bin_works()
    {
        Assert.NotNull(RecycleBin.Query(Path.GetPathRoot(Path.GetTempPath())!));
    }
}
