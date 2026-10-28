using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSlim.Core.Platform;

/// <summary>实际位置和预期的路径不一致（路径中途经过了链接），为安全起见不处理。</summary>
public sealed class PathRedirectedException(string message) : IOException(message);

/// <summary>
/// 一个清理目标目录，以及它在磁盘上的实际位置（上级目录里的链接已经解析掉）。
/// 目标里的每个路径，实际位置都应该是 <see cref="ResolvedRoot"/> 下同样的相对路径。
/// </summary>
public sealed record PathAnchor(string Root, string ResolvedRoot)
{
    /// <summary>打开目录，读出它的实际位置。</summary>
    public static PathAnchor For(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return new PathAnchor(root, HandleOperations.ResolveDirectory(root));
    }

    /// <summary><paramref name="path"/> 应该对应的实际位置。</summary>
    /// <exception cref="PathRedirectedException">路径不在这个目标目录里。</exception>
    public string Expected(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Rules.PathExpander.IsSameOrInside(full, Root))
        {
            throw new PathRedirectedException($"{full} 不在 {Root} 中，为安全起见不处理");
        }

        var relative = Path.GetRelativePath(Root, full);
        return relative == "." ? ResolvedRoot : Path.Join(ResolvedRoot, relative);
    }
}

/// <summary>
/// 按句柄删除和移动：先打开目标（不跟进最后一级链接），核对它的实际位置正是预期的路径，再通过同一个句柄操作。
/// 核对之后不再按路径重新查找，所以核对的和操作的一定是同一个文件。
/// </summary>
public static partial class HandleOperations
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int FileAttributeTagInfoClass = 9;

    /// <summary>目录的实际位置（解析其中的链接）。</summary>
    public static string ResolveDirectory(string directory)
    {
        using var handle = Open(directory, FileReadAttributes, FlagBackupSemantics);
        return FinalPath(handle, directory);
    }

    /// <summary>永久删除 <paramref name="anchor"/> 里的一个文件。</summary>
    /// <exception cref="PathRedirectedException">文件是链接，或者实际位置和预期的不一致。</exception>
    public static void DeleteFile(string path, PathAnchor anchor)
    {
        using var handle = OpenVerified(path, anchor, directory: false);
        MarkForDeletion(handle, path);
    }

    /// <summary>删除 <paramref name="anchor"/> 里的一个空目录；目录不空、是链接、不在预期位置或删除失败时返回 false。</summary>
    public static bool TryDeleteEmptyDirectory(string path, PathAnchor anchor)
    {
        try
        {
            using var handle = OpenVerified(path, anchor, directory: true);

            // 目录里还有任何东西都会失败（ERROR_DIR_NOT_EMPTY），不会连带删除。
            MarkForDeletion(handle, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把 <paramref name="anchor"/> 里的一个文件改名到 <paramref name="destination"/>（必须在同一块盘上，目标已存在时失败）。</summary>
    /// <exception cref="PathRedirectedException">文件是链接，或者实际位置和预期的不一致。</exception>
    public static void MoveFile(string path, PathAnchor anchor, string destination)
    {
        using var handle = OpenVerified(path, anchor, directory: false);
        Rename(handle, path, Path.GetFullPath(destination));
    }

    /// <summary>只核对实际位置，用于只能按路径调用的系统操作（移到回收站）。</summary>
    /// <exception cref="PathRedirectedException">文件是链接，或者实际位置和预期的不一致。</exception>
    public static void Verify(string path, PathAnchor anchor) => OpenVerified(path, anchor, directory: false).Dispose();

    private static SafeFileHandle OpenVerified(string path, PathAnchor anchor, bool directory)
    {
        var expected = anchor.Expected(path);

        // 总是带上 BACKUP_SEMANTICS：期望是文件、实际却是目录（或目录联接）时也能打开，再按属性拒绝。
        var handle = Open(path, Delete | FileReadAttributes, FlagOpenReparsePoint | FlagBackupSemantics);
        try
        {
            var attributes = Attributes(handle, path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new PathRedirectedException($"{path} 是链接，为安全起见不处理");
            }

            if (((attributes & FileAttributes.Directory) != 0) != directory)
            {
                throw new IOException(directory ? $"{path} 不是目录" : $"{path} 是目录");
            }

            var actual = FinalPath(handle, path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new PathRedirectedException($"{path} 的实际位置是 {actual}，和预期的不一致（路径中途经过了链接），为安全起见不处理");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFile(Win32Path(Path.GetFullPath(path)), access, ShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw ErrorFor(error, path);
        }

        return handle;
    }

    private static unsafe FileAttributes Attributes(SafeFileHandle handle, string path)
    {
        FileAttributeTagInfo info;
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, &info, (uint)sizeof(FileAttributeTagInfo)))
        {
            throw ErrorFor(Marshal.GetLastPInvokeError(), path);
        }

        return (FileAttributes)info.FileAttributes;
    }

    private static unsafe string FinalPath(SafeFileHandle handle, string path)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* p = buffer)
            {
                length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                throw ErrorFor(Marshal.GetLastPInvokeError(), path);
            }

            // 缓冲区够用时返回的长度不含结尾的 \0；不够时返回需要的长度（含 \0）。
            if (length < buffer.Length)
            {
                return NormalizeFinalPath(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>去掉 <c>\\?\</c> 前缀，和 <see cref="Path.GetFullPath(string)"/> 的写法一致，方便比较。</summary>
    internal static string NormalizeFinalPath(string finalPath)
    {
        var path = finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..]
            : finalPath;
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>加上 <c>\\?\</c> 前缀，长路径也能直接交给 Windows API。</summary>
    private static string Win32Path(string fullPath) =>
        fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
        : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
        : @"\\?\" + fullPath;

    private static unsafe void MarkForDeletion(SafeFileHandle handle, string path)
    {
        byte deleteFile = 1; // FILE_DISPOSITION_INFO 只有一个 BOOLEAN 字段；句柄关闭时删除。
        if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, &deleteFile, sizeof(byte)))
        {
            throw ErrorFor(Marshal.GetLastPInvokeError(), path);
        }
    }

    private static unsafe void Rename(SafeFileHandle handle, string path, string destination)
    {
        // RootDirectory 为空时文件名必须是完整路径；用 NT 路径写法，不依赖 Win32 层的转换。
        var name = @"\??\" + destination;
        var nameOffset = FileRenameInfoNameOffset;
        var size = nameOffset + ((name.Length + 1) * sizeof(char));
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var header = (FileRenameInfoHeader*)buffer;
            header->ReplaceIfExists = 0;
            header->RootDirectory = IntPtr.Zero;
            header->FileNameLength = (uint)(name.Length * sizeof(char));
            name.AsSpan().CopyTo(new Span<char>(buffer + nameOffset, name.Length));
            if (!SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)size))
            {
                throw ErrorFor(Marshal.GetLastPInvokeError(), path);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>FILE_RENAME_INFO 中文件名的偏移：紧跟在 FileNameLength 之后，不是结构体的大小。</summary>
    internal static int FileRenameInfoNameOffset =>
        (int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.FileNameLength)) + sizeof(uint);

    private static Exception ErrorFor(int error, string path) => error switch
    {
        2 => new FileNotFoundException($"找不到 {path}", path),
        3 => new DirectoryNotFoundException($"找不到 {path} 所在的目录"),
        5 => new UnauthorizedAccessException($"没有权限处理 {path}"),
        _ => new IOException($"{path}：{new Win32Exception(error).Message}", unchecked((int)0x80070000) | error),
    };

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, void* info, uint size);

    /// <summary>FILE_ATTRIBUTE_TAG_INFO。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>FILE_RENAME_INFO 的定长部分（64 位布局）。ReplaceIfExists 和 Flags 共用前 4 个字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileRenameInfoHeader
    {
        public uint ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }
}
