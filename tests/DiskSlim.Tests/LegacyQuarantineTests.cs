using System.Text;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Presentation;
using DiskSlim.Core.Quarantine;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

/// <summary>
/// 旧版（脚本版）工具留在磁盘根目录 .diskslim-quarantine 里的批次：清单格式照搬真实电脑上找到的那一份
/// （带 BOM、属性名首字母大写、一项可能是整个文件夹）。
/// </summary>
public sealed class LegacyQuarantineTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly QuarantineStore _store;
    private readonly SafetyGuard _guard = SafetyGuard.ForCurrentUser();
    private readonly string _batch;

    public LegacyQuarantineTests()
    {
        _store = new QuarantineStore(_dir.PathOf("quarantine"), _dir.PathOf(".diskslim-quarantine"));
        _batch = _dir.CreateDirectory(".diskslim-quarantine/20260909-203406");
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>旧版批次：一个文件夹（里面有只读文件）和一个安装包。</summary>
    private QuarantineSession CreateBatch(string createdAt = "2026-09-09T20:34:13")
    {
        _dir.WriteFile(".diskslim-quarantine/20260909-203406/0001_codex-runtimes/bin/tool.exe", 700, attributes: FileAttributes.ReadOnly);
        _dir.WriteFile(".diskslim-quarantine/20260909-203406/0001_codex-runtimes/readme.txt", 300);
        _dir.WriteFile(".diskslim-quarantine/20260909-203406/0153_MuMu-setup.exe", 5000);
        WriteManifest($$"""
            {
                "Stamp":  "20260909-203406",
                "CreatedAt":  "{{createdAt}}",
                "ScanFile":  "D:\\硬盘瘦身\\out\\scan.json",
                "Volume":  "C:",
                "Items":  [
                              { "Original":  "{{Json(_dir.PathOf("home/.cache/codex-runtimes"))}}", "Stored":  "0001_codex-runtimes", "Bytes":  1000 },
                              { "Original":  "{{Json(_dir.PathOf("home/Downloads/MuMu-setup.exe"))}}", "Stored":  "0153_MuMu-setup.exe", "Bytes":  5000 }
                          ]
            }
            """);
        return Assert.Single(_store.ListSessions());
    }

    private void WriteManifest(string json) =>
        File.WriteAllText(Path.Join(_batch, "manifest.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static string Json(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void Old_batches_are_listed_next_to_the_new_ones()
    {
        var session = CreateBatch();

        Assert.True(session.IsLegacy);
        Assert.Equal("20260909-203406", session.Id);
        Assert.Equal(new DateTime(2026, 9, 9, 20, 34, 13), session.CreatedAt.LocalDateTime);
        Assert.Equal(6000, session.TotalBytes);
        Assert.Equal(2, session.Items.Count);
        Assert.True(session.Items[0].IsDirectory);
        Assert.False(session.Items[1].IsDirectory);
        Assert.Contains("旧版工具移入的 2 项", session.Description);
        Assert.Equal("2 项", DisplayText.QuarantineCount(session));
        Assert.NotNull(_store.Find("20260909-203406"));
    }

    [Fact]
    public void Restore_moves_whole_folders_back_and_cleans_up_the_old_quarantine()
    {
        var session = CreateBatch();

        var result = _store.Restore(session, _guard);

        Assert.Equal(2, result.Restored);
        Assert.Empty(result.Problems);
        Assert.Equal(700, new FileInfo(_dir.PathOf("home/.cache/codex-runtimes/bin/tool.exe")).Length);
        Assert.True(File.Exists(_dir.PathOf("home/Downloads/MuMu-setup.exe")));
        Assert.Empty(_store.ListSessions());
        Assert.False(Directory.Exists(_dir.PathOf(".diskslim-quarantine")));
    }

    [Fact]
    public void Restore_never_overwrites_a_folder_that_came_back()
    {
        var session = CreateBatch();
        _dir.WriteFile("home/.cache/codex-runtimes/new.bin", 1); // 程序又重新下载了一份

        var result = _store.Restore(session, _guard);

        Assert.Equal(1, result.Restored);
        Assert.Contains("同名文件", Assert.Single(result.Problems).Reason);
        Assert.Single(Assert.Single(_store.ListSessions()).Items);
    }

    [Fact]
    public void Purge_deletes_nested_read_only_files_and_the_empty_old_quarantine()
    {
        var session = CreateBatch();

        var result = _store.Purge([session], NullOperationLog.Instance);

        Assert.Equal((1, 6000L), (result.Purged, result.PurgedBytes));
        Assert.Empty(result.Problems);
        Assert.False(Directory.Exists(_dir.PathOf(".diskslim-quarantine")));
    }

    [Fact]
    public void Expired_old_batches_count_as_reclaimable_in_their_own_location()
    {
        CreateBatch(createdAt: DateTime.Now.AddDays(-18).ToString("yyyy-MM-ddTHH:mm:ss"));
        var rule = new CleanupRule { Id = "expired", Name = "到期", Risk = RiskLevel.Low, Action = CleanupAction.PurgeQuarantine, Paths = [PathExpander.QuarantineToken], MinAgeDays = 7 };

        var evaluated = new RuleEvaluator(Path.GetPathRoot(_dir.Root)!, quarantine: _store).Evaluate(rule);

        var match = Assert.Single(evaluated.Matches);
        Assert.Equal(_dir.PathOf(".diskslim-quarantine"), match.Path);
        Assert.Equal(6000, match.ReclaimableBytes);
    }

    [Fact]
    public void Tampered_manifest_entries_are_ignored()
    {
        _dir.WriteFile("outside.bin", 10);
        _dir.WriteFile(".diskslim-quarantine/20260909-203406/0002_ok.bin", 20);
        WriteManifest($$"""
            { "Stamp": "20260909-203406", "Items": [
                { "Original": "{{Json(_dir.PathOf("x.bin"))}}", "Stored": "..\\..\\outside.bin", "Bytes": 10 },
                { "Original": "relative\\path.bin", "Stored": "0002_ok.bin", "Bytes": 20 },
                { "Original": "{{Json(_dir.PathOf("ok.bin"))}}", "Stored": "0002_ok.bin", "Bytes": 20 }
            ] }
            """);

        var session = Assert.Single(_store.ListSessions());

        var item = Assert.Single(session.Items);
        Assert.Equal(_dir.PathOf("ok.bin"), item.OriginalPath);
    }

    [Fact]
    public void Folders_without_a_manifest_are_not_treated_as_batches()
    {
        _dir.WriteFile(".diskslim-quarantine/something-else/file.bin", 10);

        Assert.Empty(_store.ListSessions());
    }

    [Fact]
    public void The_old_quarantine_is_protected_from_cleaning_rules()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.NotNull(_guard.Check(Path.Join(QuarantineStore.LegacyRootFor(drive), "20260909-203406")));
        Assert.Equal(Path.Join(drive, ".diskslim-quarantine"), QuarantineStore.ForDrive(drive).LegacyRoot);
    }
}
