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
    public void Help_and_version_flags()
    {
        Assert.True(CliParser.Parse(["-h"]).Options!.ShowHelp);
        Assert.True(CliParser.Parse(["/?"]).Options!.ShowHelp);
        Assert.True(CliParser.Parse(["--version"]).Options!.ShowVersion);
    }
}
