using DiskSlim.Cli;
using DiskSlim.Core.Formatting;

namespace DiskSlim.Tests;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    [InlineData(-2048L, "-2.00 KB")]
    public void ByteSize_formats_with_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("中文", 4)]
    [InlineData("C盘 清理", 8)]
    [InlineData("（）", 4)]
    [InlineData("", 0)]
    public void TextWidth_counts_cjk_as_two_columns(string text, int expected)
    {
        Assert.Equal(expected, TextWidth.Of(text));
    }

    [Fact]
    public void PadRight_aligns_by_display_width()
    {
        Assert.Equal("中文  ", TextWidth.PadRight("中文", 6));
        Assert.Equal("ab    ", TextWidth.PadRight("ab", 6));
    }

    [Fact]
    public void Truncate_keeps_within_width()
    {
        var truncated = TextWidth.Truncate("网易云音乐缓存目录", 10);

        Assert.True(TextWidth.Of(truncated) <= 10);
        Assert.EndsWith("...", truncated);
        Assert.Equal("短", TextWidth.Truncate("短", 10));
    }

    [Fact]
    public void TruncateMiddle_keeps_start_and_end_of_paths()
    {
        var path = @"C:\Users\someone\AppData\Local\Microsoft\Edge\User Data\Default\Cache";

        var truncated = TextWidth.TruncateMiddle(path, 30);

        Assert.True(TextWidth.Of(truncated) <= 30);
        Assert.StartsWith(@"C:\Users", truncated);
        Assert.EndsWith(@"Default\Cache", truncated);
        Assert.Contains("...", truncated);
    }

    [Fact]
    public void Wrap_breaks_by_width_and_keeps_punctuation_off_line_starts()
    {
        var lines = TextWidth.Wrap("一二三四五。六七八", 10);

        Assert.Equal(["一二三四五。", "六七八"], lines);
        Assert.All(TextWidth.Wrap(new string('字', 25), 10), line => Assert.True(TextWidth.Of(line) <= 10));
    }

    [Theory]
    [InlineData(0.0, 4, "░░░░")]
    [InlineData(0.5, 4, "██░░")]
    [InlineData(1.2, 4, "████")]
    public void Bar_has_fixed_length(double ratio, int length, string expected)
    {
        Assert.Equal(expected, ConsoleReportRenderer.Bar(ratio, length));
    }
}
