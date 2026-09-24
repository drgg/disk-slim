namespace DiskSlim.Cli;

/// <summary>在同一行刷新的进度提示，写到标准错误，不影响重定向出去的报告。</summary>
internal sealed class ProgressDisplay(TextWriter writer, int width)
{
    private static readonly string[] Frames = ["|", "/", "-", "\\"];
    private int _frame;
    private int _lastWidth;

    public void Update(string text)
    {
        var line = TextWidth.Truncate($"{Frames[_frame++ % Frames.Length]} {text}", Math.Max(10, width - 1));
        var lineWidth = TextWidth.Of(line);
        writer.Write("\r" + line + new string(' ', Math.Max(0, _lastWidth - lineWidth)));
        writer.Flush();
        _lastWidth = lineWidth;
    }

    public void Clear()
    {
        if (_lastWidth == 0)
        {
            return;
        }

        writer.Write("\r" + new string(' ', _lastWidth) + "\r");
        writer.Flush();
        _lastWidth = 0;
    }
}
