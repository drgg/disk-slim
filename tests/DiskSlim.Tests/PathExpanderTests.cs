using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

public sealed class PathExpanderTests : IDisposable
{
    private const string Variable = "DISKSLIM_TEST_ROOT";
    private readonly TestDirectory _dir = new();

    public PathExpanderTests() => Environment.SetEnvironmentVariable(Variable, _dir.Root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        _dir.Dispose();
    }

    [Fact]
    public void Expands_environment_variables_and_forward_slashes()
    {
        _dir.CreateDirectory("Cache");

        Assert.Equal([_dir.PathOf("Cache")], PathExpander.Expand($"%{Variable}%/Cache"));
    }

    [Fact]
    public void Wildcard_segment_matches_directories_case_insensitively()
    {
        _dir.CreateDirectory("User Data/Default/Cache");
        _dir.CreateDirectory("User Data/Profile 1/cache");
        _dir.CreateDirectory("User Data/Other/NotCache");

        var matches = PathExpander.Expand($"%{Variable}%/User Data/*/Cache");

        Assert.Equal([_dir.PathOf("User Data/Default/Cache"), _dir.PathOf("User Data/Profile 1/cache")], matches, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wildcard_in_last_segment_matches_files()
    {
        _dir.WriteFile("Explorer/thumbcache_32.db", 1);
        _dir.WriteFile("Explorer/thumbcache_96.db", 1);
        _dir.WriteFile("Explorer/other.db", 1);

        var matches = PathExpander.Expand($"%{Variable}%/Explorer/thumbcache_*.db");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.StartsWith("thumbcache_", Path.GetFileName(m)));
    }

    [Fact]
    public void Wildcard_does_not_expand_into_junctions()
    {
        _dir.CreateDirectory("apps/real/Cache");
        _dir.CreateJunction("apps/alias", _dir.PathOf("apps/real"));

        var matches = PathExpander.Expand($"%{Variable}%/apps/*/Cache");

        Assert.Equal([_dir.PathOf("apps/real/Cache")], matches);
    }

    [Fact]
    public void Includes_hidden_directories()
    {
        var hidden = _dir.CreateDirectory("hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.Directory);
        _dir.CreateDirectory("hidden/Cache");

        Assert.Single(PathExpander.Expand($"%{Variable}%/*/Cache"));
    }

    [Theory]
    [InlineData("%DISKSLIM_UNDEFINED_VARIABLE%/Cache")]
    [InlineData("relative/path")]
    [InlineData(@"C:\definitely\does\not\exist\diskslim")]
    public void Returns_nothing_for_unresolvable_patterns(string pattern)
    {
        Assert.Empty(PathExpander.Expand(pattern));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\A\B", @"c:\a\", true)]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    public void IsSameOrInside_compares_whole_path_segments(string path, string ancestor, bool expected)
    {
        Assert.Equal(expected, PathExpander.IsSameOrInside(path, ancestor));
    }

    [Fact]
    public void Recognizes_recycle_bin_token()
    {
        Assert.True(PathExpander.IsRecycleBin(" ::recyclebin "));
        Assert.False(PathExpander.IsRecycleBin("C:\\$Recycle.Bin"));
    }
}
