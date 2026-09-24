using System.Security.AccessControl;
using System.Security.Principal;
using DiskSlim.Core.Scanning;

namespace DiskSlim.Tests;

public sealed class TreeScannerTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Sums_sizes_bottom_up()
    {
        _dir.WriteFile("a.bin", 100);
        _dir.WriteFile("sub/b.bin", 200);
        _dir.WriteFile("sub/deep/c.bin", 300);
        _dir.CreateDirectory("empty");

        var result = new TreeScanner().Scan(_dir.Root);

        var root = result.Root;
        Assert.Equal(600, root.TotalBytes);
        Assert.Equal(100, root.OwnBytes);
        Assert.Equal(3, root.TotalFileCount);
        Assert.Equal(4, result.Stats.Directories);
        Assert.Equal(3, result.Stats.Files);
        Assert.Equal(600, result.Stats.Bytes);

        var sub = root.FindDescendant(_dir.PathOf("sub"));
        Assert.NotNull(sub);
        Assert.Equal(500, sub.TotalBytes);
        Assert.Equal(200, sub.OwnBytes);
        Assert.Equal(_dir.PathOf("sub"), sub.FullPath);
        Assert.Equal(300, root.FindDescendant(_dir.PathOf("sub/deep"))!.TotalBytes);
    }

    [Fact]
    public void Counts_hidden_and_system_files()
    {
        _dir.WriteFile("visible.bin", 10);
        _dir.WriteFile("hidden.bin", 20, attributes: FileAttributes.Hidden | FileAttributes.System);

        var result = new TreeScanner().Scan(_dir.Root);

        Assert.Equal(30, result.Root.TotalBytes);
    }

    [Fact]
    public void Reclaimable_bytes_only_include_files_older_than_cutoff()
    {
        var now = DateTime.UtcNow;
        _dir.WriteFile("old.tmp", 100, now.AddDays(-10));
        _dir.WriteFile("sub/old2.tmp", 40, now.AddDays(-3));
        _dir.WriteFile("new.tmp", 50, now);

        var result = new TreeScanner(new ScanOptions { ReclaimableBefore = now.AddDays(-2) }).Scan(_dir.Root);

        Assert.Equal(190, result.Root.TotalBytes);
        Assert.Equal(140, result.Root.TotalReclaimableBytes);
    }

    [Fact]
    public void Without_cutoff_everything_is_reclaimable()
    {
        _dir.WriteFile("a.tmp", 70);

        var result = new TreeScanner().Scan(_dir.Root);

        Assert.Equal(70, result.Root.TotalReclaimableBytes);
    }

    [Fact]
    public void Keeps_the_largest_files_in_descending_order()
    {
        _dir.WriteFile("s.bin", 10);
        _dir.WriteFile("m.bin", 500);
        _dir.WriteFile("x/l.bin", 900);
        _dir.WriteFile("x/y/xl.bin", 1200);

        var result = new TreeScanner(new ScanOptions { LargestFileCount = 2 }).Scan(_dir.Root);

        Assert.Equal([1200L, 900L], result.LargestFiles.Select(f => f.Bytes));
        Assert.Equal(_dir.PathOf("x/y/xl.bin"), result.LargestFiles[0].Path);
    }

    [Fact]
    public void Does_not_follow_directory_junctions()
    {
        using var outside = new TestDirectory();
        outside.WriteFile("big.bin", 1000);
        _dir.WriteFile("own.bin", 5);
        _dir.CreateJunction("link", outside.Root);

        var result = new TreeScanner().Scan(_dir.Root);

        Assert.Equal(5, result.Root.TotalBytes);
        Assert.Equal(1, result.Stats.SkippedLinks);
        Assert.Null(result.Root.FindDescendant(_dir.PathOf("link")));
    }

    [Fact]
    public void Junction_inside_scanned_tree_is_not_counted_twice()
    {
        _dir.WriteFile("data/file.bin", 300);
        _dir.CreateJunction("alias", _dir.PathOf("data"));

        var result = new TreeScanner().Scan(_dir.Root);

        Assert.Equal(300, result.Root.TotalBytes);
    }

    [Fact]
    public void Records_inaccessible_directories_and_keeps_going()
    {
        _dir.WriteFile("ok/a.bin", 10);
        _dir.WriteFile("locked/secret.bin", 999);
        var locked = new DirectoryInfo(_dir.PathOf("locked"));
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);

        try
        {
            var result = new TreeScanner().Scan(_dir.Root);

            Assert.Equal(10, result.Root.TotalBytes);
            Assert.Equal(1, result.Stats.InaccessibleDirectories);
            Assert.Equal(1, result.Root.TotalInaccessibleCount);
            var error = Assert.Single(result.Errors);
            Assert.Equal(locked.FullName, error.Path);
            Assert.Equal(5, error.ErrorCode); // ERROR_ACCESS_DENIED
            Assert.NotEqual(0, result.Root.FindDescendant(locked.FullName)!.ErrorCode);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void Throws_when_cancelled()
    {
        _dir.WriteFile("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new TreeScanner().Scan(_dir.Root, cts.Token));
    }

    [Fact]
    public void Throws_for_missing_root()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new TreeScanner().Scan(_dir.PathOf("nope")));
    }

    [Fact]
    public void Handles_wide_trees_with_many_workers()
    {
        for (var i = 0; i < 200; i++)
        {
            _dir.WriteFile($"d{i}/e{i % 7}/f.bin", i + 1);
        }

        var result = new TreeScanner(new ScanOptions { MaxDegreeOfParallelism = 8 }).Scan(_dir.Root);

        Assert.Equal(Enumerable.Range(1, 200).Sum(), result.Root.TotalBytes);
        Assert.Equal(200, result.Stats.Files);
        Assert.Equal(401, result.Stats.Directories);
    }

    [Fact]
    public void FindDescendant_returns_null_outside_the_tree()
    {
        _dir.WriteFile("a/b.bin", 1);
        var result = new TreeScanner().Scan(_dir.PathOf("a"));

        Assert.Null(result.Root.FindDescendant(_dir.Root));
        Assert.Null(result.Root.FindDescendant(@"Z:\elsewhere"));
        Assert.Same(result.Root, result.Root.FindDescendant(_dir.PathOf("a")));
    }
}
