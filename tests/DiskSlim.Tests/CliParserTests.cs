using DiskSlim.Cli;

namespace DiskSlim.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void Defaults_to_system_drive_with_everything_enabled()
    {
        var options = CliParser.Parse([]).Options!;

        Assert.Equal(Path.GetPathRoot(Environment.SystemDirectory), options.DriveRoot);
        Assert.Equal(15, options.Top);
        Assert.True(options.IncludeRules);
        Assert.True(options.IncludeSpaceMap);
    }

    [Theory]
    [InlineData("d", @"D:\")]
    [InlineData("D:", @"D:\")]
    [InlineData(@"e:\", @"E:\")]
    [InlineData("C:/", @"C:\")]
    public void Normalizes_drive_letters(string input, string expected)
    {
        Assert.Equal(expected, CliParser.Parse(["--drive", input]).Options!.DriveRoot);
    }

    [Fact]
    public void Supports_short_options_and_inline_values()
    {
        var options = CliParser.Parse(["-d", "D", "-n", "30", "--json=out.json", "--md", "out.md", "-v", "--rules-only"]).Options!;

        Assert.Equal(@"D:\", options.DriveRoot);
        Assert.Equal(30, options.Top);
        Assert.Equal("out.json", options.JsonPath);
        Assert.Equal("out.md", options.MarkdownPath);
        Assert.True(options.Verbose);
        Assert.False(options.IncludeSpaceMap);
        Assert.True(options.IncludeRules);
    }

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData("--drive")]
    [InlineData("--drive", "CD")]
    [InlineData("--top", "0")]
    [InlineData("--top", "abc")]
    [InlineData("--json")]
    [InlineData("--rules-only", "--map-only")]
    public void Rejects_invalid_arguments(params string[] args)
    {
        var result = CliParser.Parse(args);

        Assert.Null(result.Options);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Parses_the_clean_command()
    {
        var options = CliParser.Parse(["clean", "--rule", "npm-cache,pip-cache", "--rule", "NPM-CACHE", "--rule=recycle-bin", "--execute", "-y", "--log", "x.jsonl"]).Options!;

        Assert.Equal(CliCommand.Clean, options.Command);
        Assert.Equal(["npm-cache", "pip-cache", "recycle-bin"], options.RuleIds);
        Assert.True(options.Execute);
        Assert.True(options.AssumeYes);
        Assert.Equal("x.jsonl", options.LogPath);
    }

    [Fact]
    public void Clean_defaults_to_dry_run()
    {
        var options = CliParser.Parse(["clean"]).Options!;

        Assert.False(options.Execute);
        Assert.Empty(options.RuleIds);
    }

    [Theory]
    [InlineData("clean", "--top", "5")] // 只能用于 scan
    [InlineData("clean", "--json", "a.json")]
    [InlineData("--execute")] // 只能用于 clean
    [InlineData("scan", "--rule", "x")]
    [InlineData("clean", "--yes")] // --yes 必须和 --execute 一起用
    [InlineData("clean", "--rule")]
    [InlineData("clean", "--rule", ",")]
    public void Rejects_options_for_the_wrong_command(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Theory]
    [InlineData(new[] { "quarantine" }, "List", null)]
    [InlineData(new[] { "quarantine", "list" }, "List", null)]
    [InlineData(new[] { "quarantine", "restore", "20260925-101500-ab12" }, "Restore", "20260925-101500-ab12")]
    [InlineData(new[] { "quarantine", "purge", "abc", "--execute", "-y" }, "Purge", "abc")]
    public void Parses_quarantine_commands(string[] args, string verb, string? id)
    {
        var options = CliParser.Parse(args).Options!;

        Assert.Equal(CliCommand.Quarantine, options.Command);
        Assert.Equal(Enum.Parse<QuarantineVerb>(verb), options.QuarantineVerb);
        Assert.Equal(id, options.QuarantineId);
    }

    [Fact]
    public void Parses_quarantine_purge_of_expired_sessions()
    {
        var options = CliParser.Parse(["quarantine", "purge", "--expired", "--days", "14"]).Options!;

        Assert.True(options.PurgeExpired);
        Assert.Equal(14, options.ExpiryDays);
        Assert.False(options.Execute);
    }

    [Theory]
    [InlineData("quarantine", "restore")]                    // 缺少批次
    [InlineData("quarantine", "purge")]                      // 没说删什么
    [InlineData("quarantine", "purge", "abc", "--all")]      // 只能选一种
    [InlineData("quarantine", "frobnicate")]
    [InlineData("quarantine", "list", "abc")]
    [InlineData("quarantine", "--rule", "x")]                // --rule 只能用于 clean
    [InlineData("clean", "--expired")]                       // --expired 只能用于 quarantine
    [InlineData("scan", "extra-positional")]
    public void Rejects_invalid_quarantine_arguments(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Fact]
    public void Parses_system_and_dupes_commands()
    {
        var list = CliParser.Parse(["system"]).Options!;
        var run = CliParser.Parse(["system", "run", "component-store", "--execute"]).Options!;
        var dupes = CliParser.Parse(["dupes", @"D:\照片", "E:\\", "--min-size", "10MB", "--remove"]).Options!;

        Assert.Equal((CliCommand.System, (string?)null), (list.Command, list.SystemTaskId));
        Assert.Equal("component-store", run.SystemTaskId);
        Assert.True(run.Execute);
        Assert.Equal([@"D:\照片", "E:\\"], dupes.DupesRoots);
        Assert.Equal(10L << 20, dupes.MinFileSize);
        Assert.True(dupes.RemoveDuplicates);
    }

    [Theory]
    [InlineData("system", "--execute")]                                   // 没有指定任务
    [InlineData("system", "run")]
    [InlineData("system", "frob")]
    [InlineData("system", "run", "x", "--result-file", "r.json")]         // 助手模式必须同时有 --execute --yes
    [InlineData("dupes", "--execute")]                                    // 必须和 --remove 一起用
    [InlineData("dupes", "--min-size", "big")]
    public void Rejects_invalid_system_and_dupes_arguments(params string[] args)
    {
        Assert.NotNull(CliParser.Parse(args).Error);
    }

    [Theory]
    [InlineData("4096", 4096L)]
    [InlineData("500KB", 500L * 1024)]
    [InlineData("10mb", 10L << 20)]
    [InlineData("1.5G", 3L << 29)]
    [InlineData("2 GB", 2L << 30)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.Equal(expected, CliParser.ParseSize(text));
    }

    [Fact]
    public void Help_and_version_flags()
    {
        Assert.True(CliParser.Parse(["-h"]).Options!.ShowHelp);
        Assert.True(CliParser.Parse(["/?"]).Options!.ShowHelp);
        Assert.True(CliParser.Parse(["--version"]).Options!.ShowVersion);
    }
}
