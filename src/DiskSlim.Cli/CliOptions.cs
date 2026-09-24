using System.Globalization;
using DiskSlim.Core.Platform;

namespace DiskSlim.Cli;

internal sealed record CliOptions
{
    public string DriveRoot { get; init; } = SystemInfo.SystemDriveRoot;

    public int Top { get; init; } = 15;

    public string? RulesFile { get; init; }

    public string? JsonPath { get; init; }

    public string? MarkdownPath { get; init; }

    public bool IncludeRules { get; init; } = true;

    public bool IncludeSpaceMap { get; init; } = true;

    public bool Verbose { get; init; }

    public bool NoColor { get; init; }

    public bool ListRules { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }
}

internal sealed record ParseResult(CliOptions? Options, string? Error);

internal static class CliParser
{
    public const string HelpText = """
        DiskSlim 硬盘瘦身 —— 只读扫描磁盘空间，找出可以清理的内容。不会修改或删除任何文件。

        用法：diskslim [选项]

        选项：
          -d, --drive <盘符>    要分析的磁盘，默认是系统盘
          -n, --top <数量>      每个列表显示多少条，默认 15
          -r, --rules <文件>    使用自定义规则文件（JSON），默认使用内置规则
              --json <文件>     同时把完整报告保存为 JSON
              --md <文件>       同时把报告保存为 Markdown
              --rules-only      只检查清理规则，跳过全盘空间分布（快很多）
              --map-only        只做全盘空间分布，跳过清理规则
          -v, --verbose         显示每条规则命中的具体路径，以及全部无法读取的目录
              --no-color        不使用颜色
              --list-rules      列出当前加载的规则后退出
          -h, --help            显示帮助
              --version         显示版本

        示例：
          diskslim
          diskslim --rules-only
          diskslim -d D --top 30 --md report.md

        以管理员身份运行可以统计到更多系统目录；按 Ctrl+C 可随时取消。

        """;

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        var onlyFlags = 0;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string? TakeValue()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                return i + 1 < args.Count ? args[++i] : null;
            }

            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    options = options with { ShowHelp = true };
                    break;

                case "--version":
                    options = options with { ShowVersion = true };
                    break;

                case "-d" or "--drive":
                {
                    var value = TakeValue();
                    if (value is null)
                    {
                        return Fail("--drive 需要一个盘符，例如 --drive D");
                    }

                    if (NormalizeDrive(value) is not { } root)
                    {
                        return Fail($"无效的盘符：{value}");
                    }

                    options = options with { DriveRoot = root };
                    break;
                }

                case "-n" or "--top":
                {
                    var value = TakeValue();
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var top) || top is < 1 or > 1000)
                    {
                        return Fail("--top 需要 1 到 1000 之间的整数");
                    }

                    options = options with { Top = top };
                    break;
                }

                case "-r" or "--rules":
                {
                    var value = TakeValue();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return Fail("--rules 需要一个规则文件路径");
                    }

                    options = options with { RulesFile = value };
                    break;
                }

                case "--json":
                {
                    var value = TakeValue();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return Fail("--json 需要一个输出文件路径");
                    }

                    options = options with { JsonPath = value };
                    break;
                }

                case "--md":
                {
                    var value = TakeValue();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return Fail("--md 需要一个输出文件路径");
                    }

                    options = options with { MarkdownPath = value };
                    break;
                }

                case "--rules-only":
                    options = options with { IncludeSpaceMap = false };
                    onlyFlags++;
                    break;

                case "--map-only":
                    options = options with { IncludeRules = false };
                    onlyFlags++;
                    break;

                case "-v" or "--verbose":
                    options = options with { Verbose = true };
                    break;

                case "--no-color":
                    options = options with { NoColor = true };
                    break;

                case "--list-rules":
                    options = options with { ListRules = true };
                    break;

                default:
                    return Fail($"未知选项：{args[i]}。运行 diskslim --help 查看用法。");
            }
        }

        if (onlyFlags > 1)
        {
            return Fail("--rules-only 和 --map-only 不能同时使用");
        }

        return new ParseResult(options, null);
    }

    /// <summary>把 "d"、"D:"、"d:\"、"D:/" 统一成 "D:\"。</summary>
    internal static string? NormalizeDrive(string value)
    {
        var v = value.Trim().TrimEnd('\\', '/');
        if (v.EndsWith(':'))
        {
            v = v[..^1];
        }

        return v.Length == 1 && char.IsAsciiLetter(v[0]) ? char.ToUpperInvariant(v[0]) + @":\" : null;
    }

    private static ParseResult Fail(string message) => new(null, message);
}
