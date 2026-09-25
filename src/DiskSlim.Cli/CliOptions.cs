using System.Globalization;
using DiskSlim.Core.Platform;

namespace DiskSlim.Cli;

internal enum CliCommand
{
    Scan,
    Clean,
    Quarantine,
    Dupes,
}

internal enum QuarantineVerb
{
    List,
    Restore,
    Purge,
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

    // ───── quarantine ─────
    public QuarantineVerb QuarantineVerb { get; init; } = QuarantineVerb.List;

    public string? QuarantineId { get; init; }

    public bool PurgeExpired { get; init; }

    public bool PurgeAll { get; init; }

    /// <summary>--expired 时多少天算到期。</summary>
    public int ExpiryDays { get; init; } = 7;

    // ───── dupes ─────

    /// <summary>在这些目录里查找重复文件；为空时查用户目录。</summary>
    public IReadOnlyList<string> DupesRoots { get; init; } = [];

    public long MinFileSize { get; init; } = 1024 * 1024;

    /// <summary>把建议移除的副本移入隔离区（配合 --execute）。</summary>
    public bool RemoveDuplicates { get; init; }
}

internal sealed record ParseResult(CliOptions? Options, string? Error);

internal static class CliParser
{
    public const string HelpText = """
        DiskSlim 硬盘瘦身 —— 分析磁盘空间，安全地清理缓存和临时文件。

        用法：
          diskslim [scan] [选项]                  只读扫描，生成报告（默认）
          diskslim clean [选项]                   清理。默认只模拟，加 --execute 才会真正执行
          diskslim quarantine                     列出隔离区
          diskslim quarantine restore <批次>      把一批文件恢复到原来的位置（不会覆盖任何文件）
          diskslim quarantine purge <批次>        彻底删除一批，释放空间（默认只模拟，加 --execute 执行）
          diskslim quarantine purge --expired     彻底删除超过 7 天的批次（--days 修改天数）
          diskslim quarantine purge --all         彻底删除隔离区中的全部内容
          diskslim dupes [路径...]                查找重复文件（默认查用户目录），只读
          diskslim dupes [路径...] --remove       把建议移除的副本移入隔离区（默认只模拟，加 --execute 执行）

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

        dupes 选项：
              --min-size <大小>  只查不小于这个大小的文件，默认 1MB（例如 500KB、10MB、1GB）
              --remove          移除建议的副本：每组保留最早创建的一份，其余移入隔离区；
                                组里有聊天记录等受保护的文件时整组不动
          -n, --top <数量>      显示多少组，默认 15

        “需要确认”的项目默认移入隔离区：可以随时恢复，彻底删除后才释放空间；
        超过 7 天的隔离内容会作为“可放心清理”的项目出现在 diskslim clean 里。

        示例：
          diskslim                                 查看完整报告
          diskslim clean                           模拟清理所有“可放心清理”的项目
          diskslim clean --execute                 真正清理它们（会先让你确认）
          diskslim clean --rule recycle-bin,nuget-packages --execute

        以管理员身份运行可以统计和清理更多系统目录；按 Ctrl+C 可随时取消。

        """;

    /// <summary>只能用于特定命令的选项；不在表里的选项所有命令都能用。</summary>
    private static readonly Dictionary<string, CliCommand[]> CommandSpecific = new(StringComparer.Ordinal)
    {
        ["-n"] = [CliCommand.Scan, CliCommand.Dupes],
        ["--top"] = [CliCommand.Scan, CliCommand.Dupes],
        ["--json"] = [CliCommand.Scan],
        ["--md"] = [CliCommand.Scan],
        ["--rules-only"] = [CliCommand.Scan],
        ["--map-only"] = [CliCommand.Scan],
        ["--list-rules"] = [CliCommand.Scan],
        ["--rule"] = [CliCommand.Clean],
        ["--log"] = [CliCommand.Clean, CliCommand.Quarantine, CliCommand.Dupes],
        ["--execute"] = [CliCommand.Clean, CliCommand.Quarantine, CliCommand.Dupes],
        ["-y"] = [CliCommand.Clean, CliCommand.Quarantine, CliCommand.Dupes],
        ["--yes"] = [CliCommand.Clean, CliCommand.Quarantine, CliCommand.Dupes],
        ["--expired"] = [CliCommand.Quarantine],
        ["--all"] = [CliCommand.Quarantine],
        ["--days"] = [CliCommand.Quarantine],
        ["--min-size"] = [CliCommand.Dupes],
        ["--remove"] = [CliCommand.Dupes],
    };

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        var start = 0;
        if (args.Count > 0 && args[0] is "scan" or "clean" or "quarantine" or "dupes")
        {
            options = options with
            {
                Command = args[0] switch
                {
                    "clean" => CliCommand.Clean,
                    "quarantine" => CliCommand.Quarantine,
                    "dupes" => CliCommand.Dupes,
                    _ => CliCommand.Scan,
                },
            };
            start = 1;
        }

        var onlyFlags = 0;
        var ruleIds = new List<string>();
        var positionals = new List<string>();

        for (var i = start; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            if (CommandSpecific.TryGetValue(arg, out var allowed) && !allowed.Contains(options.Command))
            {
                return Fail($"{arg} 只能用于 {string.Join(" 或 ", allowed.Select(c => "diskslim " + c.ToString().ToLowerInvariant()))}");
            }

            if (options.Command is CliCommand.Quarantine or CliCommand.Dupes && !arg.StartsWith('-') && arg != "/?")
            {
                positionals.Add(arg);
                continue;
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

                case "--expired":
                    options = options with { PurgeExpired = true };
                    break;

                case "--all":
                    options = options with { PurgeAll = true };
                    break;

                case "--remove":
                    options = options with { RemoveDuplicates = true };
                    break;

                case "--min-size":
                {
                    var value = TakeValue();
                    if (ParseSize(value) is not { } size)
                    {
                        return Fail("--min-size 需要一个大小，例如 500KB、10MB、1GB");
                    }

                    options = options with { MinFileSize = size };
                    break;
                }

                case "--days":
                {
                    var value = TakeValue();
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 0 or > 3650)
                    {
                        return Fail("--days 需要 0 到 3650 之间的整数");
                    }

                    options = options with { ExpiryDays = days };
                    break;
                }

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

        if (options.Command == CliCommand.Quarantine)
        {
            var parsedQuarantine = ParseQuarantine(options, positionals);
            if (parsedQuarantine.Error is not null)
            {
                return parsedQuarantine;
            }

            options = parsedQuarantine.Options!;
        }

        if (options.Command == CliCommand.Dupes)
        {
            if (options.Execute && !options.RemoveDuplicates)
            {
                return Fail("--execute 需要和 --remove 一起使用（dupes 默认只查找，不移动任何文件）");
            }

            options = options with { DupesRoots = positionals };
        }

        return new ParseResult(options with { RuleIds = ruleIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, null);
    }

    private static ParseResult ParseQuarantine(CliOptions options, List<string> positionals)
    {
        var verb = positionals.Count == 0 ? "list" : positionals[0];
        var id = positionals.Count > 1 ? positionals[1] : null;
        if (positionals.Count > 2)
        {
            return Fail($"多余的参数：{string.Join(" ", positionals.Skip(2))}");
        }

        switch (verb)
        {
            case "list":
                return id is null ? new(options with { QuarantineVerb = QuarantineVerb.List }, null) : Fail("quarantine list 不需要批次");

            case "restore":
                if (id is null)
                {
                    return Fail("请指定要恢复的批次，例如 diskslim quarantine restore 20260925-101500-ab12（运行 diskslim quarantine 查看）");
                }

                return new(options with { QuarantineVerb = QuarantineVerb.Restore, QuarantineId = id }, null);

            case "purge":
                var choices = (id is null ? 0 : 1) + (options.PurgeExpired ? 1 : 0) + (options.PurgeAll ? 1 : 0);
                if (choices != 1)
                {
                    return Fail("请指定要彻底删除的内容：一个批次、--expired 或 --all（只能选一个）");
                }

                return new(options with { QuarantineVerb = QuarantineVerb.Purge, QuarantineId = id }, null);

            default:
                return Fail($"未知的 quarantine 操作：{verb}。可用的有 list、restore、purge。");
        }
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

    /// <summary>解析 "500KB"、"10MB"、"1.5GB"、"4096" 这样的大小（1024 进制）。</summary>
    internal static long? ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        (string Suffix, long Factor)[] units = [("GB", 1L << 30), ("G", 1L << 30), ("MB", 1L << 20), ("M", 1L << 20), ("KB", 1L << 10), ("K", 1L << 10), ("B", 1)];
        var factor = 1L;
        foreach (var (suffix, unitFactor) in units)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text[..^suffix.Length];
                factor = unitFactor;
                break;
            }
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? (long)(number * factor)
            : null;
    }

    private static ParseResult Fail(string message) => new(null, message);
}
