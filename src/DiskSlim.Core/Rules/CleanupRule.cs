using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSlim.Core.Rules;

/// <summary>清理风险等级。</summary>
[JsonConverter(typeof(RiskLevelJsonConverter))]
public enum RiskLevel
{
    /// <summary>可放心清理：缓存、临时文件，删除后会自动重建。</summary>
    Low,

    /// <summary>需要确认：删除后要重新下载，或者需要用户判断是否还有用。</summary>
    Medium,

    /// <summary>仅供参考：用户数据或系统文件，工具不应自动清理。</summary>
    High,
}

/// <summary>读写 "low" / "medium" / "high"，读取时不区分大小写。</summary>
internal sealed class RiskLevelJsonConverter : JsonConverter<RiskLevel>
{
    public override RiskLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return Enum.TryParse<RiskLevel>(text, ignoreCase: true, out var risk) && Enum.IsDefined(risk) && !int.TryParse(text, out _)
            ? risk
            : throw new JsonException($"risk 只能是 low、medium 或 high，而不是 {text ?? reader.TokenType.ToString()}");
    }

    public override void Write(Utf8JsonWriter writer, RiskLevel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
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
    /// 以及特殊值 <c>::RecycleBin</c>（回收站）。
    /// </summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>必填：清理工具不能替规则作者假定一个风险等级。</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>只把至少这么多天前修改过的文件计为“可释放”；为空时全部计入。</summary>
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
