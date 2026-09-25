using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Duplicates;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly QuarantineStore _store;
    private readonly SafetyGuard _guard = SafetyGuard.ForCurrentUser();

    public DuplicateTests() => _store = new QuarantineStore(_dir.PathOf("quarantine"));

    public void Dispose() => _dir.Dispose();

    private static readonly DuplicateSearchOptions SmallFiles = new() { MinFileSize = 1 };

    /// <summary>写入内容可控的文件：用 <paramref name="seed"/> 生成字节，<paramref name="flipAt"/> 处改一个字节。</summary>
    private string Write(string relative, int size, byte seed = 7, int? flipAt = null, DateTime? created = null)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        if (flipAt is { } position)
        {
            bytes[position] ^= 0xFF;
        }

        var path = _dir.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (created is { } time)
        {
            File.SetCreationTimeUtc(path, time);
        }

        return path;
    }

    private DuplicateSearchResult Find(DuplicateSearchOptions? options = null) =>
        new DuplicateFinder().Find([_dir.Root], options ?? SmallFiles);

    private static void Run(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", .. command.Split(' ')]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Finds_identical_files_and_ignores_files_that_only_look_alike()
    {
        const int size = 100_000;
        Write("a/photo.jpg", size);
        Write("b/photo (1).jpg", size);
        Write("c/copy.jpg", size);
        Write("d/same-edges.jpg", size, flipAt: size / 2);   // 头尾一样，中间不同
        Write("e/different-start.jpg", size, flipAt: 10);    // 开头不同
        Write("f/other-size.jpg", size + 1);

        var result = Find();

        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2L * size, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Path.Contains("same-edges"));
        Assert.Equal(64, group.Hash.Length); // SHA-256
    }

    [Fact]
    public void Small_files_below_the_threshold_are_ignored()
    {
        Write("a/x.bin", 500);
        Write("b/x.bin", 500);

        Assert.Empty(Find(new DuplicateSearchOptions { MinFileSize = 1000 }).Groups);
        Assert.Single(Find().Groups);
    }

    [Fact]
    public void Small_files_are_compared_by_full_content()
    {
        Write("a/tiny.txt", 20);
        Write("b/tiny.txt", 20);
        Write("c/tiny.txt", 20, flipAt: 19);

        Assert.Equal(2, Assert.Single(Find().Groups).Files.Count);
    }

    [Fact]
    public void Hard_links_are_not_duplicates()
    {
        var original = Write("a/big.bin", 50_000);
        Run($"mkdir {_dir.PathOf("b")} && mklink /H {_dir.PathOf("b\\link.bin")} {original}");

        var result = Find();

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.HardLinksIgnored);
    }

    [Fact]
    public void Does_not_follow_junctions()
    {
        Write("real/file.bin", 50_000);
        _dir.CreateJunction("alias", _dir.PathOf("real"));

        Assert.Empty(Find().Groups);
    }

    [Fact]
    public void Nested_roots_are_scanned_once()
    {
        Write("a/x.bin", 1000);
        Write("a/sub/y.bin", 1000, seed: 9);

        var result = new DuplicateFinder().Find([_dir.PathOf("a"), _dir.PathOf("a/sub"), _dir.Root], SmallFiles);

        Assert.Empty(result.Groups);
        Assert.Equal([Path.TrimEndingDirectorySeparator(_dir.Root)], result.Roots);
    }

    [Fact]
    public void Skips_forbidden_zones_entirely()
    {
        Write("zone/x.bin", 1000);
        Write("zone/y.bin", 1000);
        var guard = new SafetyGuard([], [_dir.PathOf("zone")]);

        Assert.Empty(Find(SmallFiles with { Guard = guard }).Groups);
    }

    [Fact]
    public void Files_in_protected_areas_are_listed_but_never_suggested_for_removal()
    {
        Write("chat/received.pdf", 5000);
        Write("downloads/received.pdf", 5000);
        var options = SmallFiles with { ProtectedAreas = [new ProtectedArea(_dir.PathOf("chat"), "属于聊天记录")] };

        var group = Assert.Single(Find(options).Groups);

        Assert.True(group.HasProtectedFiles);
        Assert.Equal("属于聊天记录", group.Files.Single(f => f.Path.Contains("chat")).ProtectedReason);
        Assert.Empty(group.SuggestedRemovals);
    }

    [Fact]
    public void Suggests_keeping_the_original_and_removing_only_obvious_copies()
    {
        Write("work/report.docx", 3000, created: DateTime.UtcNow.AddDays(-1));
        Write("documents/report.docx", 3000, created: DateTime.UtcNow.AddYears(-1));
        Write("desktop/report - 副本.docx", 3000, created: DateTime.UtcNow.AddYears(-2)); // 更早，但名字表明是副本

        var group = Assert.Single(Find().Groups);

        Assert.Contains("documents", group.SuggestedKeeper.Path);
        var removal = Assert.Single(group.SuggestedRemovals);
        Assert.Contains("副本", removal.Path);
        // work/report.docx 也是重复的，但不明显是副本：列出来，不自动建议移除。
        Assert.DoesNotContain(group.SuggestedRemovals, f => f.Path.Contains(@"\work\"));
    }

    [Theory]
    [InlineData("report (1)", true)]
    [InlineData("report（2）", true)]
    [InlineData("report - 副本", true)]
    [InlineData("report - 副本 (3)", true)]
    [InlineData("report - Copy", true)]
    [InlineData("report_copy", true)]
    [InlineData("Copy of report", true)]
    [InlineData("复件 报告", false)]
    [InlineData("report", false)]
    [InlineData("copyright notice", false)]
    [InlineData("photo (2021)", false)]
    public void Recognizes_copy_file_names(string name, bool expected)
    {
        Write($"a/{name}.txt", 100);
        Write("b/original.txt", 100);

        var file = Assert.Single(Find().Groups).Files.Single(f => f.Path.Contains(@"\a\"));

        Assert.Equal(expected, file.LooksLikeCopy);
    }

    [Fact]
    public void Files_in_the_downloads_folder_count_as_copies()
    {
        Write("docs/setup.exe", 2000);
        Write("downloads/setup.exe", 2000);
        var options = SmallFiles with { CopyLocations = [_dir.PathOf("downloads")] };

        var group = Assert.Single(Find(options).Groups);

        Assert.Contains(@"\docs\", group.SuggestedKeeper.Path);
        Assert.Contains(@"\downloads\", Assert.Single(group.SuggestedRemovals).Path);
    }

    [Theory]
    [InlineData("SaveData_backup_20260918")]
    [InlineData("备份")]
    [InlineData("Backups")]
    public void Backups_are_protected(string folder)
    {
        Write("game/save.dat", 2000);
        Write($"game/{folder}/save (1).dat", 2000);

        var group = Assert.Single(Find().Groups);

        Assert.Contains("备份", group.Files.Single(f => f.Path.Contains(folder)).ProtectedReason);
        Assert.Empty(group.SuggestedRemovals);
    }

    [Fact]
    public void Program_libraries_are_protected_and_tool_folders_are_skipped()
    {
        Write("app1/ffmpeg.dll", 3000);
        Write("app2/ffmpeg.dll", 3000);
        Write("project/node_modules/lib/a.js", 3000, seed: 3);
        Write("project/.git/objects/a", 3000, seed: 3);
        var options = SmallFiles with
        {
            ProtectedExtensions = new HashSet<string>([".dll"], StringComparer.OrdinalIgnoreCase),
            SkipDirectoryNames = new HashSet<string>(["node_modules"], StringComparer.OrdinalIgnoreCase),
            SkipDotDirectories = true,
        };

        var group = Assert.Single(Find(options).Groups);

        Assert.All(group.Files, f => Assert.Contains("程序库", f.ProtectedReason));
    }

    [Fact]
    public void Default_protected_areas_cover_app_data_but_not_user_folders()
    {
        var areas = DuplicateFinder.ProtectedAreasFrom(RuleLoader.LoadDefault());
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        Assert.DoesNotContain(areas, a => PathExpander.IsSameOrInside(downloads, a.Path) || PathExpander.IsSameOrInside(a.Path, downloads));
    }

    [Fact]
    public void Remover_quarantines_selected_copies_and_keeps_one()
    {
        Write("a/x.bin", 4000);
        Write("b/x.bin", 4000);
        Write("c/x.bin", 4000);
        var group = Assert.Single(Find().Groups);
        var remove = group.Files.Where(f => !f.Path.Contains(@"\a\")).ToList();

        var preview = new DuplicateRemover(_store, _guard).Remove([new DuplicateSelection(group, remove)], dryRun: true);
        Assert.Equal((2, 8000L), (preview.Files, preview.Bytes));
        Assert.True(File.Exists(_dir.PathOf("b/x.bin")));

        var result = new DuplicateRemover(_store, _guard).Remove([new DuplicateSelection(group, remove)], dryRun: false);

        Assert.Equal((2, 8000L), (result.Files, result.Bytes));
        Assert.True(File.Exists(_dir.PathOf("a/x.bin")));
        Assert.False(File.Exists(_dir.PathOf("b/x.bin")));
        var session = _store.Find(result.QuarantineSessionId!)!;
        Assert.Equal(2, session.Items.Count);

        _store.Restore(session, _guard);
        Assert.True(File.Exists(_dir.PathOf("b/x.bin")));
    }

    [Fact]
    public void Remover_never_removes_every_copy()
    {
        Write("a/x.bin", 4000);
        Write("b/x.bin", 4000);
        var group = Assert.Single(Find().Groups);

        var result = new DuplicateRemover(_store, _guard).Remove([new DuplicateSelection(group, group.Files)], dryRun: false);

        Assert.Equal(0, result.Files);
        Assert.True(File.Exists(_dir.PathOf("a/x.bin")) && File.Exists(_dir.PathOf("b/x.bin")));
        Assert.All(result.Skipped, s => Assert.Contains("至少要保留一份", s.Reason));
    }

    [Fact]
    public void Remover_leaves_the_group_alone_if_the_keeper_changed()
    {
        Write("a/x.bin", 4000);
        Write("b/x.bin", 4000);
        var group = Assert.Single(Find().Groups);
        var keeper = group.Files.Single(f => f.Path.Contains(@"\a\"));
        File.AppendAllText(keeper.Path, "改过了");

        var result = new DuplicateRemover(_store, _guard).Remove([new DuplicateSelection(group, group.Files.Except([keeper]).ToList())], dryRun: false);

        Assert.Equal(0, result.Files);
        Assert.True(File.Exists(_dir.PathOf("b/x.bin")));
        Assert.Contains("被修改过", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public void Remover_skips_files_changed_since_the_scan_and_protected_files()
    {
        Write("a/x.bin", 4000);
        Write("b/x.bin", 4000);
        Write("chat/x.bin", 4000);
        var options = SmallFiles with { ProtectedAreas = [new ProtectedArea(_dir.PathOf("chat"), "属于聊天记录")] };
        var group = Assert.Single(Find(options).Groups);
        File.SetLastWriteTimeUtc(_dir.PathOf("b/x.bin"), DateTime.UtcNow.AddMinutes(5));

        var remove = group.Files.Where(f => !f.Path.Contains(@"\a\")).ToList();
        var result = new DuplicateRemover(_store, _guard).Remove([new DuplicateSelection(group, remove)], dryRun: false);

        Assert.Equal(0, result.Files);
        Assert.True(File.Exists(_dir.PathOf("b/x.bin")) && File.Exists(_dir.PathOf("chat/x.bin")));
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void BY_HANDLE_FILE_INFORMATION_matches_the_sdk_layout()
    {
        Assert.Equal(52, Marshal.SizeOf<FileIdentity.ByHandleFileInformation>());
        Assert.Equal(28, (int)Marshal.OffsetOf<FileIdentity.ByHandleFileInformation>(nameof(FileIdentity.ByHandleFileInformation.VolumeSerialNumber)));
        Assert.Equal(40, (int)Marshal.OffsetOf<FileIdentity.ByHandleFileInformation>(nameof(FileIdentity.ByHandleFileInformation.NumberOfLinks)));
        Assert.Equal(48, (int)Marshal.OffsetOf<FileIdentity.ByHandleFileInformation>(nameof(FileIdentity.ByHandleFileInformation.FileIndexLow)));
    }

    [Fact]
    public void File_identity_reports_link_count_for_hard_links()
    {
        var original = Write("a/big.bin", 100);
        Run($"mkdir {_dir.PathOf("b")} && mklink /H {_dir.PathOf("b\\link.bin")} {original}");

        using var first = File.OpenHandle(original);
        using var second = File.OpenHandle(_dir.PathOf("b/link.bin"));
        var a = FileIdentity.Of(first)!.Value;
        var b = FileIdentity.Of(second)!.Value;

        Assert.True(a.IsSameFile(b));
        Assert.Equal(2u, a.LinkCount);
    }
}
