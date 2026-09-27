using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSlim.Core.Rules;

/// <summary>清理风险等级。</summary>
[JsonConverter(typeof(KebabCaseEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    /// <summary>可放心清理：缓存、临时文件，删除后会自动重建。</summary>
    Low,

    /// <summary>需要确认：删除后要重新下载，或者需要用户判断是否还有用。</summary>
    Medium,

    /// <summary>仅供参考：用户数据或系统文件，工具不应自动清理。</summary>
    High,
}

/// <summary>工具如何清理一条规则命中的内容。</summary>
[JsonConverter(typeof(KebabCaseEnumConverter<CleanupAction>))]
public enum CleanupAction
{
    /// <summary>工具不清理，只给出建议（默认值，也是最安全的值）。</summary>
    None = 0,

    /// <summary>永久删除命中的文件，保留规则指定的目录本身。适合缓存和临时文件。</summary>
    Delete,

    /// <summary>把命中的文件移到回收站，可以找回。适合用户文件。</summary>
    Recycle,

    /// <summary>清空回收站，只能用于 <c>::RecycleBin</c>。</summary>
    EmptyRecycleBin,

    /// <summary>
    /// 把命中的文件移入隔离区（同一块盘上，瞬间完成），可以随时恢复；确认没问题后再彻底删除。适合“需要确认”的内容。
    /// </summary>
    Quarantine,

    /// <summary>彻底删除隔离区中超过 minAgeDays 天的批次，只能用于 <c>::Quarantine</c>。</summary>
    PurgeQuarantine,
}

/// <summary>用短横线小写名称读写枚举（例如 EmptyRecycleBin ↔ "empty-recycle-bin"），读取时不区分大小写。</summary>
internal sealed class KebabCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ByName = Enum.GetValues<TEnum>()
        .ToDictionary(v => ToKebabCase(v.ToString()), StringComparer.OrdinalIgnoreCase);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return text is not null && ByName.TryGetValue(text, out var value)
            ? value
            : throw new JsonException($"只能是 {string.Join("、", ByName.Keys)} 之一，而不是 {text ?? reader.TokenType.ToString()}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToKebabCase(value.ToString()));

    internal static string ToKebabCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>一条清理规则：描述某一类可以清理的内容在哪里、有多安全、应该怎么清理。</summary>
public sealed record CleanupRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    // 以下可选属性在 setter 里把 null 换成默认值：JSON 源生成器会给文件中没写的 init 属性显式赋 null。
    public string Category { get; init => field = value ?? "其他"; } = "其他";

    /// <summary>
    /// 路径模板。支持环境变量（%LOCALAPPDATA%）、通配符（* 和 ?，可出现在任意一级）、/ 或 \ 分隔，
    /// 开头的 <c>&lt;drives&gt;</c> 这类前缀（见 <see cref="PathTokens"/>），以及特殊值 <c>::RecycleBin</c>（回收站）。
    /// </summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>
    /// 路径命中的是按版本号命名的程序文件夹（例如 D:\Quark\7.1.7.975）时只处理旧版本：
    /// 最新的版本、以及同一目录下的程序（.exe）自己的版本对应的文件夹都保留——程序可能还没切换到新版本。
    /// 清空了的旧版本文件夹本身也会删除。
    /// </summary>
    public bool OldVersionsOnly { get; init; }

    /// <summary>必填：清理工具不能替规则作者假定一个风险等级。</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>工具如何清理；不写就是 <see cref="CleanupAction.None"/>，工具只给建议。</summary>
    public CleanupAction Action { get; init; }

    /// <summary>
    /// 只把至少这么多天前修改（或创建）过的文件计为“可释放”，清理时也只删除这些文件；为空时全部计入。
    /// </summary>
    public int? MinAgeDays { get; init; }

    /// <summary>这些程序运行时不宜清理（进程名，可带或不带 .exe）。</summary>
    public IReadOnlyList<string> Processes { get; init => field = value ?? []; } = [];

    /// <summary>给用户的清理建议。</summary>
    public string Advice { get; init => field = value ?? ""; } = "";
}

public sealed record RuleSet
{
    public int Version { get; init; } = 1;

    public required IReadOnlyList<CleanupRule> Rules { get; init; }
}
