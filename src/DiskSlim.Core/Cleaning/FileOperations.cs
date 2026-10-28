using DiskSlim.Core.Platform;

namespace DiskSlim.Core.Cleaning;

/// <summary>清理用到的所有写操作。集中在这里，便于审查，也便于测试时替换。</summary>
/// <remarks>
/// 每个操作都带上所在的清理目标（<see cref="PathAnchor"/>）：执行前核对文件的实际位置，
/// 路径中途经过链接、实际位置不在目标里时拒绝执行（<see cref="PathRedirectedException"/>）。
/// </remarks>
public interface IFileOperations
{
    /// <summary>永久删除文件。文件不存在时不报错。</summary>
    void DeleteFile(string path, PathAnchor anchor);

    /// <summary>删除空目录；目录不空、不在预期位置或删除失败时返回 false。</summary>
    bool TryDeleteEmptyDirectory(string path, PathAnchor anchor);

    void MoveToRecycleBin(string path, PathAnchor anchor);

    RecycleBinInfo? QueryRecycleBin(string driveRoot);

    void EmptyRecycleBin(string driveRoot);
}

public sealed class WindowsFileOperations : IFileOperations
{
    public void DeleteFile(string path, PathAnchor anchor)
    {
        try
        {
            HandleOperations.DeleteFile(path, anchor);
        }
        catch (FileNotFoundException)
        {
            // 已经不在了。
        }
    }

    public bool TryDeleteEmptyDirectory(string path, PathAnchor anchor) => HandleOperations.TryDeleteEmptyDirectory(path, anchor);

    /// <summary>回收站只能按路径交给系统处理：先按句柄核对实际位置，再移过去。</summary>
    public void MoveToRecycleBin(string path, PathAnchor anchor)
    {
        HandleOperations.Verify(path, anchor);
        RecycleBin.MoveToRecycleBin(path);
    }

    public RecycleBinInfo? QueryRecycleBin(string driveRoot) => RecycleBin.Query(driveRoot);

    public void EmptyRecycleBin(string driveRoot) => RecycleBin.Empty(driveRoot);
}
