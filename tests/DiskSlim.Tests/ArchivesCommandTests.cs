using System.IO.Compression;
using DiskSlim.Cli;

namespace DiskSlim.Tests;

/// <summary>端到端：diskslim archives 只读查找和 --remove 的模拟运行（不会碰真实的隔离区）。</summary>
public sealed class ArchivesCommandTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (int ExitCode, string Output, string Errors) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["archives", _dir.Root, "--min-size", "1", "--no-color", .. args], stdout, stderr, new StringReader(""), interactive: false);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private void CreateExtractedZip()
    {
        var time = new DateTimeOffset(2024, 3, 1, 10, 20, 30, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 3, 1)));
        var zipPath = _dir.PathOf("dl/game.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("data.pak", CompressionLevel.NoCompression);
            entry.LastWriteTime = time;
            using var stream = entry.Open();
            stream.Write(new byte[5000]);
        }

        var extracted = _dir.WriteFile("dl/game/data.pak", 5000);
        File.SetLastWriteTime(extracted, time.LocalDateTime);
        _dir.WriteFile("dl/other.rar", 3000);
        _dir.WriteFile("dl/other/content.bin", 4000);
    }

    [Fact]
    public void Lists_extracted_archives_without_touching_anything()
    {
        CreateExtractedZip();

        var (exitCode, output, errors) = Run();

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal("", errors);
        Assert.Contains("[可移除]", output);
        Assert.Contains("[请确认]", output);
        Assert.Contains("game.zip", output);
        Assert.Contains("--remove", output);
        Assert.True(File.Exists(_dir.PathOf("dl/game.zip")));
    }

    [Fact]
    public void Remove_without_execute_is_a_dry_run_of_verified_archives_only()
    {
        CreateExtractedZip();

        var (exitCode, output, _) = Run("--remove");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("将把 1 个压缩包", output);
        Assert.Contains("模拟运行", output);
        Assert.True(File.Exists(_dir.PathOf("dl/game.zip")));
        Assert.True(File.Exists(_dir.PathOf("dl/other.rar")));
    }

    [Fact]
    public void Missing_folders_are_reported()
    {
        var (exitCode, _, errors) = Run(_dir.PathOf("nope"));

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains("目录不存在", errors);
    }
}
