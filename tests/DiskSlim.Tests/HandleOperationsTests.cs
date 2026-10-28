using DiskSlim.Core.Platform;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Tests;

/// <summary>按句柄删除和移动：只处理实际位置正是预期路径的文件，经过链接到达的一律拒绝。</summary>
public sealed class HandleOperationsTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Deletes_a_file_inside_the_target()
    {
        var file = _dir.WriteFile("cache/sub/a.tmp", 10);

        HandleOperations.DeleteFile(file, PathAnchor.For(_dir.PathOf("cache")));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Refuses_a_file_reached_through_a_link_inside_the_target()
    {
        using var outside = new TestDirectory();
        var precious = outside.WriteFile("thesis.docx", 10);
        _dir.CreateDirectory("cache");
        _dir.CreateJunction("cache/link", outside.Root);
        var anchor = PathAnchor.For(_dir.PathOf("cache"));

        Assert.Throws<PathRedirectedException>(() => HandleOperations.DeleteFile(_dir.PathOf("cache/link/thesis.docx"), anchor));
        Assert.Throws<PathRedirectedException>(() => HandleOperations.MoveFile(_dir.PathOf("cache/link/thesis.docx"), anchor, _dir.PathOf("moved.docx")));
        Assert.Throws<PathRedirectedException>(() => HandleOperations.Verify(_dir.PathOf("cache/link/thesis.docx"), anchor));

        Assert.True(File.Exists(precious));
        Assert.False(File.Exists(_dir.PathOf("moved.docx")));
    }

    [Fact]
    public void Leaves_the_link_itself_and_its_target_alone()
    {
        using var outside = new TestDirectory();
        outside.CreateDirectory("empty");
        _dir.CreateDirectory("cache");
        var link = _dir.CreateJunction("cache/link", outside.PathOf("empty"));
        var anchor = PathAnchor.For(_dir.PathOf("cache"));

        Assert.False(HandleOperations.TryDeleteEmptyDirectory(link, anchor));
        Assert.Throws<PathRedirectedException>(() => HandleOperations.DeleteFile(link, anchor));

        Assert.True(Directory.Exists(link));
        Assert.True(Directory.Exists(outside.PathOf("empty")));
    }

    [Fact]
    public void Allows_links_above_the_target()
    {
        // 例如整个 AppData 被搬到别的盘、原位置留了一个目录联接：目标目录本身的实际位置在别处是正常的。
        var file = _dir.WriteFile("real/cache/a.tmp", 10);
        _dir.CreateJunction("alias", _dir.PathOf("real"));

        HandleOperations.DeleteFile(_dir.PathOf("alias/cache/a.tmp"), PathAnchor.For(_dir.PathOf("alias/cache")));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Refuses_paths_outside_the_target()
    {
        var other = _dir.WriteFile("other/a.tmp", 10);
        var anchor = PathAnchor.For(_dir.CreateDirectory("cache"));

        Assert.Throws<PathRedirectedException>(() => HandleOperations.DeleteFile(other, anchor));
        Assert.Throws<PathRedirectedException>(() => HandleOperations.DeleteFile(_dir.PathOf("cache/../other/a.tmp"), anchor));

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Deletes_only_empty_directories()
    {
        var anchor = PathAnchor.For(_dir.CreateDirectory("cache"));
        var empty = _dir.CreateDirectory("cache/empty");
        var kept = _dir.WriteFile("cache/full/a.tmp", 10);

        Assert.True(HandleOperations.TryDeleteEmptyDirectory(empty, anchor));
        Assert.False(HandleOperations.TryDeleteEmptyDirectory(_dir.PathOf("cache/full"), anchor));

        Assert.False(Directory.Exists(empty));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Can_delete_the_target_directory_itself_when_empty()
    {
        var target = _dir.CreateDirectory("app/1.2.3.4");

        Assert.True(HandleOperations.TryDeleteEmptyDirectory(target, PathAnchor.For(target)));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Moves_a_file_by_handle_without_replacing_anything()
    {
        var file = _dir.WriteFile("cache/a.tmp", 10);
        var taken = _dir.WriteFile("quarantine/taken.tmp", 3);
        var destination = _dir.PathOf("quarantine/000001.tmp");
        var anchor = PathAnchor.For(_dir.PathOf("cache"));

        Assert.ThrowsAny<IOException>(() => HandleOperations.MoveFile(file, anchor, taken));
        HandleOperations.MoveFile(file, anchor, destination);

        Assert.False(File.Exists(file));
        Assert.Equal(10, new FileInfo(destination).Length);
        Assert.Equal(3, new FileInfo(taken).Length);
    }

    [Fact]
    public void Missing_files_report_not_found()
    {
        var anchor = PathAnchor.For(_dir.CreateDirectory("cache"));

        Assert.Throws<FileNotFoundException>(() => HandleOperations.DeleteFile(_dir.PathOf("cache/gone.tmp"), anchor));
    }

    [Fact]
    public void Quarantine_refuses_a_redirected_file_and_keeps_it_in_place()
    {
        using var outside = new TestDirectory();
        var precious = outside.WriteFile("thesis.docx", 10);
        _dir.CreateDirectory("cache");
        _dir.CreateJunction("cache/link", outside.Root);
        var store = new QuarantineStore(_dir.PathOf("quarantine"));
        var writer = store.BeginSession("test", DateTimeOffset.Now);

        Assert.Throws<PathRedirectedException>(() =>
            writer.Move(_dir.PathOf("cache/link/thesis.docx"), 10, "test", PathAnchor.For(_dir.PathOf("cache"))));

        Assert.True(File.Exists(precious));
        Assert.Equal(0, writer.Count);
        Assert.Empty(store.ListSessions());
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me\AppData\Local\Temp", @"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"\\?\C:\", @"C:\")]
    [InlineData(@"\\?\UNC\server\share\dir\", @"\\server\share\dir")]
    public void Final_paths_are_normalized_like_full_paths(string finalPath, string expected)
    {
        Assert.Equal(expected, HandleOperations.NormalizeFinalPath(finalPath));
    }
}
