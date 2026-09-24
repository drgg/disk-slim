using System.Globalization;
using System.Text;

namespace DiskSlim.Cli;

/// <summary>按终端显示宽度处理文本：中文等全角字符占 2 列。</summary>
internal static class TextWidth
{
    private const string Ellipsis = "...";

    /// <summary>不能放在行首的标点。</summary>
    private const string NoLineStart = "。，、；：？！）」』】》〉”’.,;:?!)]}";

    public static int Of(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += Of(rune);
        }

        return width;
    }

    public static int Of(Rune rune)
    {
        var value = rune.Value;
        if (value < 0x20)
        {
            return 0;
        }

        if (value < 0x1100)
        {
            return 1;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
    }

    public static string PadRight(string text, int width) => text + new string(' ', Math.Max(0, width - Of(text)));

    public static string PadLeft(string text, int width) => new string(' ', Math.Max(0, width - Of(text))) + text;

    /// <summary>超出宽度时截断末尾并加省略号。</summary>
    public static string Truncate(string text, int maxWidth)
    {
        if (Of(text) <= maxWidth)
        {
            return text;
        }

        if (maxWidth <= Ellipsis.Length)
        {
            return Ellipsis[..Math.Max(0, maxWidth)];
        }

        var builder = new StringBuilder();
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = Of(rune);
            if (width + w > maxWidth - Ellipsis.Length)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += w;
        }

        return builder.Append(Ellipsis).ToString();
    }

    /// <summary>超出宽度时保留开头和结尾、省略中间，适合显示路径。</summary>
    public static string TruncateMiddle(string text, int maxWidth)
    {
        if (Of(text) <= maxWidth)
        {
            return text;
        }

        if (maxWidth <= Ellipsis.Length + 2)
        {
            return Truncate(text, maxWidth);
        }

        var runes = text.EnumerateRunes().ToArray();
        var budget = maxWidth - Ellipsis.Length;
        var headBudget = budget / 2;
        var tailBudget = budget - headBudget;

        var head = new StringBuilder();
        var used = 0;
        var i = 0;
        for (; i < runes.Length && used + Of(runes[i]) <= headBudget; i++)
        {
            head.Append(runes[i].ToString());
            used += Of(runes[i]);
        }

        var tail = new List<Rune>();
        used = 0;
        for (var j = runes.Length - 1; j >= i && used + Of(runes[j]) <= tailBudget; j--)
        {
            tail.Insert(0, runes[j]);
            used += Of(runes[j]);
        }

        return head + Ellipsis + string.Concat(tail.Select(r => r.ToString()));
    }

    /// <summary>按显示宽度折行。句号、逗号等标点不会出现在行首（允许略微超出宽度）。</summary>
    public static IReadOnlyList<string> Wrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = Of(rune);
            var isNoLineStart = rune.IsBmp && NoLineStart.Contains((char)rune.Value);
            if (width + w > maxWidth && line.Length > 0 && !isNoLineStart)
            {
                lines.Add(line.ToString().TrimEnd());
                line.Clear();
                width = 0;
                if (rune.Value == ' ')
                {
                    continue;
                }
            }

            line.Append(rune.ToString());
            width += w;
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString().TrimEnd());
        }

        return lines;
    }

    private static bool IsWide(int value) => value is
        (>= 0x1100 and <= 0x115F) or
        (>= 0x2E80 and <= 0x303E) or
        (>= 0x3041 and <= 0x33FF) or
        (>= 0x3400 and <= 0x4DBF) or
        (>= 0x4E00 and <= 0x9FFF) or
        (>= 0xA000 and <= 0xA4CF) or
        (>= 0xAC00 and <= 0xD7A3) or
        (>= 0xF900 and <= 0xFAFF) or
        (>= 0xFE30 and <= 0xFE4F) or
        (>= 0xFF00 and <= 0xFF60) or
        (>= 0xFFE0 and <= 0xFFE6) or
        (>= 0x1F300 and <= 0x1F64F) or
        (>= 0x1F900 and <= 0x1F9FF) or
        (>= 0x20000 and <= 0x3FFFD);
}
