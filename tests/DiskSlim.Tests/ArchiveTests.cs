using System.IO.Compression;
using DiskSlim.Core.Archives;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Quarantine;

namespace DiskSlim.Tests;

/// <summary>找出已经解压过的压缩包：zip 逐个文件核对，rar、7z 只按同名文件夹判断。</summary>
public sealed class ArchiveTests : IDisposable
{
    private static readonly DateTimeOffset EntryTime = new(2024, 3, 1, 10, 20, 30, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 3, 1)));
    private static readonly ArchiveSearchOptions AnySize = new() { MinArchiveSize = 1 };
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>创建一个 zip，里面的文件是 名字 → 大小。</summary>
    private string CreateZip(string relative, params (string Name, int Size)[] entries)
    {
        var path = _dir.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, size) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = EntryTime;
            using var stream = entry.Open();
            stream.Write(new byte[size]);
        }

        return path;
    }

    /// <summary>模拟解压软件：按压缩包里的时间写出同样大小的文件。</summary>
    private void Extract(string relativeFolder, params (string Name, int Size)[] entries)
    {
        foreach (var (name, size) in entries)
        {
            var path = _dir.WriteFile(Path.Join(relativeFolder, name), size);
            File.SetLastWriteTime(path, EntryTime.LocalDateTime);
        }
    }

    private ArchiveSearchResult Find() => new ExtractedArchiveFinder().Find([_dir.Root], AnySize);

    private static readonly (string, int)[] GameFiles = [("game/data.pak", 4000), ("game/game.exe", 1500), ("readme.txt", 20)];

    [Fact]
    public void Zip_extracted_into_a_same_name_folder_is_verified()
    {
        CreateZip("downloads/整.zip", GameFiles);
        Extract("downloads/整", GameFiles);

        var archive = Assert.Single(Find().Archives);

        Assert.Equal(ExtractionEvidence.Verified, archive.Evidence);
        Assert.True(archive.SuggestRemoval);
        Assert.Equal(_dir.PathOf("downloads/整"), archive.ExtractedTo);
        Assert.Equal((3, 3), (archive.EntryCount, archive.MatchedEntries));
    }

    [Fact]
    public void Zip_extracted_right_next_to_itself_is_verified()
    {
        CreateZip("downloads/tool.zip", [("tool/tool.exe", 3000), ("tool/config.ini", 10)]);
        Extract("downloads", [("tool/tool.exe", 3000), ("tool/config.ini", 10)]);

        var archive = Assert.Single(Find().Archives);

        Assert.Equal(ExtractionEvidence.Verified, archive.Evidence);
        Assert.Equal(_dir.PathOf("downloads"), archive.ExtractedTo);
    }

    [Fact]
    public void Zip_whose_single_top_folder_was_dropped_when_extracting_is_verified()
    {
        // 有的解压软件解压到“同名文件夹”时，会去掉压缩包里多余的一层顶层文件夹。
        CreateZip("downloads/pack-v2.zip", [("pack/a.bin", 2000), ("pack/b.bin", 3000)]);
        Extract("downloads/pack-v2", [("a.bin", 2000), ("b.bin", 3000)]);

        Assert.Equal(ExtractionEvidence.Verified, Assert.Single(Find().Archives).Evidence);
    }

    [Fact]
    public void Partly_extracted_zip_is_not_suggested()
    {
        CreateZip("downloads/整.zip", GameFiles);
        Extract("downloads/整", [("game/data.pak", 4000)]);

        var archive = Assert.Single(Find().Archives);

        Assert.Equal(ExtractionEvidence.Partial, archive.Evidence);
        Assert.False(archive.SuggestRemoval);
        Assert.Equal((3, 1), (archive.EntryCount, archive.MatchedEntries));
    }

    [Fact]
    public void Files_with_the_same_size_but_another_date_do_not_count()
    {
        // 没打上的补丁包：旁边的同名文件大小一样，但不是从这个压缩包里解压出来的。
        CreateZip("game/patch.zip", [("data.pak", 4000)]);
        var original = _dir.WriteFile("game/data.pak", 4000);
        File.SetLastWriteTime(original, EntryTime.LocalDateTime.AddDays(-100));

        Assert.Empty(Find().Archives);
    }

    [Fact]
    public void Zip_that_was_never_extracted_is_not_listed()
    {
        CreateZip("downloads/setup.zip", GameFiles);

        var result = Find();

        Assert.Empty(result.Archives);
        Assert.Equal(1, result.ArchivesChecked);
    }

    [Fact]
    public void Rar_and_7z_are_judged_only_by_a_same_name_folder()
    {
        _dir.WriteFile("downloads/game.rar", 3000);
        _dir.WriteFile("downloads/game/data.bin", 5000);
        _dir.WriteFile("downloads/half.7z", 9000);
        _dir.WriteFile("downloads/half/data.bin", 1000); // 比压缩包小很多：可能没解压完
        _dir.WriteFile("downloads/lonely.rar", 3000);

        var archives = Find().Archives.ToDictionary(a => Path.GetFileName(a.Path));

        Assert.Equal(2, archives.Count);
        Assert.Equal(ExtractionEvidence.SameNameFolder, archives["game.rar"].Evidence);
        Assert.False(archives["game.rar"].SuggestRemoval); // 读不出文件列表，不自动建议
        Assert.Equal(ExtractionEvidence.Partial, archives["half.7z"].Evidence);
    }

    [Fact]
    public void Split_archives_are_handled_as_one()
    {
        _dir.WriteFile("dl/movie.part1.rar", 3000);
        _dir.WriteFile("dl/movie.part2.rar", 3000);
        _dir.WriteFile("dl/movie.part3.rar", 500);
        _dir.WriteFile("dl/movie/movie.mkv", 6600);
        _dir.WriteFile("dl/orphan.part2.rar", 3000); // 缺了第一卷
        _dir.WriteFile("dl/orphan/x.bin", 9000);
        _dir.WriteFile("dl/pack.7z.001", 1000);
        _dir.WriteFile("dl/pack.7z.002", 1000);
        _dir.WriteFile("dl/pack/a.bin", 2500);

        var archives = Find().Archives.ToDictionary(a => Path.GetFileName(a.Path));

        Assert.Equal(["movie.part1.rar", "pack.7z.001"], archives.Keys.Order());
        Assert.Equal(3, archives["movie.part1.rar"].Parts.Count);
        Assert.Equal(6500, archives["movie.part1.rar"].Bytes);
        Assert.Equal(2, archives["pack.7z.001"].Parts.Count);
    }

    [Fact]
    public void Small_archives_are_skipped_by_default()
    {
        CreateZip("downloads/tiny.zip", [("a.txt", 100)]);
        Extract("downloads/tiny", [("a.txt", 100)]);

        Assert.Empty(new ExtractedArchiveFinder().Find([_dir.Root]).Archives); // 默认只查 50 MB 以上的
    }

    [Fact]
    public void Remover_moves_every_part_into_quarantine_and_can_be_undone()
    {
        CreateZip("downloads/整.zip", GameFiles);
        Extract("downloads/整", GameFiles);
        var archive = Assert.Single(Find().Archives);
        var store = new QuarantineStore(_dir.PathOf("quarantine"));

        var preview = new ExtractedArchiveRemover(_ => store, SafetyGuard.ForCurrentUser()).Remove([archive], dryRun: true);
        Assert.True(File.Exists(archive.Path));
        Assert.Equal(1, preview.Files);

        var result = new ExtractedArchiveRemover(_ => store, SafetyGuard.ForCurrentUser()).Remove([archive], dryRun: false);

        Assert.Equal(1, result.Files);
        Assert.False(File.Exists(archive.Path));
        Assert.True(File.Exists(_dir.PathOf("downloads/整/game/data.pak"))); // 解压出来的文件不动
        var batch = Assert.Single(result.Batches);
        store.Restore(store.Find(batch.SessionId)!, SafetyGuard.ForCurrentUser());
        Assert.True(File.Exists(archive.Path));
    }

    [Fact]
    public void Remover_leaves_archives_alone_when_things_changed_after_the_search()
    {
        CreateZip("a/one.zip", GameFiles);
        Extract("a/one", GameFiles);
        CreateZip("b/two.zip", GameFiles);
        Extract("b/two", GameFiles);
        var archives = Find().Archives;
        Directory.Delete(_dir.PathOf("a/one"), recursive: true);   // 解压出来的文件夹被删了
        File.AppendAllText(_dir.PathOf("b/two.zip"), "changed");     // 压缩包被改了
        var store = new QuarantineStore(_dir.PathOf("quarantine"));

        var result = new ExtractedArchiveRemover(_ => store, SafetyGuard.ForCurrentUser()).Remove(archives, dryRun: false);

        Assert.Equal(0, result.Files);
        Assert.Equal(2, result.Skipped.Count);
        Assert.True(File.Exists(_dir.PathOf("a/one.zip")));
        Assert.True(File.Exists(_dir.PathOf("b/two.zip")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]         // zip 只精确到 2 秒
    [InlineData(3600, true)]      // 时区不同，正好差 1 小时
    [InlineData(8 * 3600 + 1, true)]
    [InlineData(5, false)]
    [InlineData(1800, false)]
    [InlineData(86400 * 3, false)]
    public void Extracted_file_times_may_differ_by_rounding_or_time_zone(int seconds, bool same)
    {
        Assert.Equal(same, ExtractedArchiveFinder.SameTime(EntryTime.LocalDateTime.AddSeconds(seconds), EntryTime));
    }

    [Fact]
    public void Desktop_page_ticks_verified_archives_and_lets_the_user_tick_name_only_ones()
    {
        CreateZip("downloads/整.zip", GameFiles);
        Extract("downloads/整", GameFiles);
        _dir.WriteFile("downloads/game.rar", 3000);
        _dir.WriteFile("downloads/game/data.bin", 5000);
        _dir.WriteFile("downloads/half.7z", 9000);
        _dir.WriteFile("downloads/half/data.bin", 1000);
        var page = new Desktop.ViewModels.ArchivesViewModel();

        page.Show(Find());

        Assert.Equal(3, page.Items.Count);
        var selected = Assert.Single(page.SelectedItems);
        Assert.EndsWith("整.zip", selected.Path);

        var rar = page.Items.Single(i => i.Path.EndsWith("game.rar", StringComparison.Ordinal));
        rar.IsSelected = true;
        Assert.True(rar.IsSelected);

        var partial = page.Items.Single(i => i.Path.EndsWith("half.7z", StringComparison.Ordinal));
        partial.IsSelected = true;
        Assert.False(partial.IsSelected); // 没解压完的不能勾选

        var store = new QuarantineStore(_dir.PathOf("quarantine"));
        var message = page.Remove(page.SelectedItems.Select(i => i.Archive).ToList(), _ => store, _dir.PathOf("logs/archives.jsonl"));

        Assert.Contains("已把 2 个文件", message);
        Assert.Single(page.Items); // 只剩没解压完的那个
    }
}
