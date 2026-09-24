using System.Globalization;
using DiskSlim.Core.Platform;

namespace DiskSlim.Cli;

internal enum CliCommand
{
    Scan,
    Clean,
}

internal sealed record CliOptions
{
    public CliCommand Command { get; init; } = CliCommand.Scan;

    public string DriveRoot { get; init; } = SystemInfo.SystemDriveRoot;

    public string? RulesFile { get; init; }

    public bool Verbose { get; init; }

    public bool NoColor { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    // ───── scan ─────
    public int Top { get; init; } = 15;

    public string? JsonPath { get; init; }

    public string? MarkdownPath { get; init; }

    public bool IncludeRules { get; init; } = true;

    public bool IncludeSpaceMap { get; init; } = true;

    public bool ListRules { get; init; }

    // ───── clean ─────

    /// <summary>点名清理的规则；为空时清理所有“可放心清理”的项目。</summary>
    public IReadOnlyList<string> RuleIds { get; init; } = [];

    /// <summary>真正执行；否则只模拟。</summary>
    public bool Execute { get; init; }

    /// <summary>执行前不再询问确认。</summary>
    public bool AssumeYes { get; init; }

    public string? LogPath { get; init; }
}

internal sealed record ParseResult(CliOptions? Options, string? Error);

internal static class CliParser
{
    public const string HelpText = """
        DiskSlim 硬盘瘦身 —— 分析磁盘空间，安全地清理缓存和临时文件。

        用法：
          diskslim [scan] [选项]     只读扫描，生成报告（默认）
          diskslim clean [选项]      清理。默认只模拟，加 --execute 才会真正执行

        通用选项：
          -d, --drive <盘符>    要分析的磁盘，默认是系统盘
          -r, --rules <文件>    使用自定义规则文件（JSON），默认使用内置规则
          -v, --verbose         显示更多细节
              --no-color        不使用颜色
          -h, --help            显示帮助
              --version         显示版本

        scan 选项：
          -n, --top <数量>      每个列表显示多少条，默认 15
              --json <文件>     同时把完整报告保存为 JSON
              --md <文件>       同时把报告保存为 Markdown
              --rules-only      只检查清理规则，跳过全盘空间分布（快很多）
              --map-only        只做全盘空间分布，跳过清理规则
              --list-rules      列出当前加载的规则后退出

        clean 选项：
              --rule <id>       点名要清理的项目，可重复或用逗号分隔。
                                不写时清理所有“可放心清理”的项目；“需要确认”的项目必须点名；
                                “仅供参考”的项目工具不会清理
              --execute         真正执行清理（不加时只模拟，不删除任何文件）
          -y, --yes             执行前不再询问确认
              --log <文件>      操作日志的保存位置，默认 %LOCALAPPDATA%\DiskSlim\logs

        示例：
          diskslim                                 查看完整报告
          diskslim clean                           模拟清理所有“可放心清理”的项目
          diskslim clean --execute                 真正清理它们（会先让你确认）
          diskslim clean --rule recycle-bin,nuget-packages --execute

        以管理员身份运行可以统计和清理更多系统目录；按 Ctrl+C 可随时取消。

        """;

    private static readonly HashSet<string> ScanOnly = new(StringComparer.Ordinal)
    {
        "-n", "--top", "--json", "--md", "--rules-only", "--map-only", "--list-rules",
    };

    private static readonly HashSet<string> CleanOnly = new(StringComparer.Ordinal)
    {
        "--rule", "--execute", "-y", "--yes", "--log",
    };

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        var start = 0;
        if (args.Count > 0 && args[0] is "scan" or "clean")
        {
            options = options with { Command = args[0] == "clean" ? CliCommand.Clean : CliCommand.Scan };
            start = 1;
        }

        var onlyFlags = 0;
        var ruleIds = new List<string>();

        for (var i = start; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            if (options.Command == CliCommand.Scan && CleanOnly.Contains(arg))
            {
                return Fail($"{arg} 只能用于 diskslim clean");
            }

            if (options.Command == CliCommand.Clean && ScanOnly.Contains(arg))
            {
                return Fail($"{arg} 只能用于 diskslim scan");
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
                    if (NonBlank(TakeValue()) is not { } value)
                    {
                        return Fail($"{arg} 需要一个规则文件路径");
                    }

                    options = options with { RulesFile = value };
                    break;
                }

                case "--json":
                {
                    if (NonBlank(TakeValue()) is not { } value)
                    {
                        return Fail("--json 需要一个输出文件路径");
                    }

                    options = options with { JsonPath = value };
                    break;
                }

                case "--md":
                {
                    if (NonBlank(TakeValue()) is not { } value)
                    {
                        return Fail("--md 需要一个输出文件路径");
                    }

                    options = options with { MarkdownPath = value };
                    break;
                }

                case "--log":
                {
                    if (NonBlank(TakeValue()) is not { } value)
                    {
                        return Fail("--log 需要一个日志文件路径");
                    }

                    options = options with { LogPath = value };
                    break;
                }

                case "--rule":
                {
                    var value = TakeValue();
                    var ids = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (ids.Length == 0)
                    {
                        return Fail("--rule 需要规则 id，例如 --rule npm-cache（运行 diskslim --list-rules 查看）");
                    }

                    ruleIds.AddRange(ids);
                    break;
                }

                case "--execute":
                    options = options with { Execute = true };
                    break;

                case "-y" or "--yes":
                    options = options with { AssumeYes = true };
                    break;

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

        if (options.AssumeYes && !options.Execute)
        {
            return Fail("--yes 需要和 --execute 一起使用");
        }

        return new ParseResult(options with { RuleIds = ruleIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, null);
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

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ParseResult Fail(string message) => new(null, message);
}
