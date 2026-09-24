using DiskSlim.Core.Platform;

namespace DiskSlim.Core.Cleaning;

/// <summary>清理用到的所有写操作。集中在这里，便于审查，也便于测试时替换。</summary>
public interface IFileOperations
{
    /// <summary>永久删除文件。文件不存在时不报错。</summary>
    void DeleteFile(string path);

    /// <summary>删除空目录；目录不空或删除失败时返回 false。</summary>
    bool TryDeleteEmptyDirectory(string path);

    void MoveToRecycleBin(string path);

    RecycleBinInfo? QueryRecycleBin(string driveRoot);

    void EmptyRecycleBin(string driveRoot);
}

public sealed class WindowsFileOperations : IFileOperations
{
    public void DeleteFile(string path) => File.Delete(path);

    public bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            // recursive: false —— 目录里还有任何东西都会失败，不会连带删除。
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void MoveToRecycleBin(string path) => RecycleBin.MoveToRecycleBin(path);

    public RecycleBinInfo? QueryRecycleBin(string driveRoot) => RecycleBin.Query(driveRoot);

    public void EmptyRecycleBin(string driveRoot) => RecycleBin.Empty(driveRoot);
}
