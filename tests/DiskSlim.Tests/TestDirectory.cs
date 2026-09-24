using System.Diagnostics;

namespace DiskSlim.Tests;

/// <summary>测试用的临时目录，释放时删除。</summary>
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "DiskSlimTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', '\\'));

    public string CreateDirectory(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>创建指定大小的文件。指定 <paramref name="timestampUtc"/> 时，创建时间和最后修改时间都设成它。</summary>
    public string WriteFile(string relative, int size, DateTime? timestampUtc = null, FileAttributes? attributes = null)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        if (timestampUtc is { } time)
        {
            File.SetCreationTimeUtc(path, time);
            File.SetLastWriteTimeUtc(path, time);
        }

        if (attributes is { } attrs)
        {
            File.SetAttributes(path, attrs);
        }

        return path;
    }

    /// <summary>把目录的创建时间和修改时间设成指定时间。</summary>
    public void SetDirectoryTime(string relative, DateTime timestampUtc)
    {
        var path = PathOf(relative);
        Directory.SetCreationTimeUtc(path, timestampUtc);
        Directory.SetLastWriteTimeUtc(path, timestampUtc);
    }

    /// <summary>用 mklink /J 创建目录联接（不需要管理员权限）。</summary>
    public string CreateJunction(string relative, string target)
    {
        var path = PathOf(relative);
        var startInfo = new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"创建目录联接失败：{error}");
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结果。
        }
    }
}
