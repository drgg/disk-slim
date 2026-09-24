namespace DiskSlim.Cli;

/// <summary>向输出写文本，可选带颜色。只有输出是真实控制台时才应启用颜色。</summary>
internal sealed class ConsoleStyle(TextWriter writer, bool useColor)
{
    public void Write(string text, ConsoleColor? color = null)
    {
        if (!useColor || color is not { } c)
        {
            writer.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = c;
        writer.Write(text);
        Console.ForegroundColor = previous;
    }

    public void WriteLine(string text = "", ConsoleColor? color = null)
    {
        Write(text, color);
        writer.WriteLine();
    }
}
