using System.Text.Json;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.Rules;

public static class RuleLoader
{
    internal const string DefaultResourceName = "DiskSlim.Core.Rules.default-rules.json";

    /// <summary>加载内置的默认规则。</summary>
    public static RuleSet LoadDefault()
    {
        using var stream = typeof(RuleLoader).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException("找不到内置规则资源：" + DefaultResourceName);
        return Load(stream, "内置规则");
    }

    public static RuleSet LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, path);
    }

    /// <exception cref="InvalidDataException">规则文件格式错误或内容不合法。</exception>
    public static RuleSet Load(Stream stream, string sourceName)
    {
        RuleSet? set;
        try
        {
            set = JsonSerializer.Deserialize(stream, DiskSlimJsonContext.Default.RuleSet);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"规则文件 {sourceName} 格式错误：{ex.Message}", ex);
        }

        if (set is null)
        {
            throw new InvalidDataException($"规则文件 {sourceName} 是空的。");
        }

        var problems = Validate(set);
        if (problems.Count > 0)
        {
            throw new InvalidDataException($"规则文件 {sourceName} 有 {problems.Count} 处问题：" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        return set;
    }

    internal static List<string> Validate(RuleSet set)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Rules.Count; i++)
        {
            var rule = set.Rules[i];
            var label = string.IsNullOrWhiteSpace(rule.Id) ? $"第 {i + 1} 条规则" : $"规则 {rule.Id}";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                problems.Add($"{label}：缺少 id");
            }
            else if (!ids.Add(rule.Id))
            {
                problems.Add($"{label}：id 重复");
            }

            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                problems.Add($"{label}：缺少 name");
            }

            if (rule.Paths.Count == 0 || rule.Paths.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{label}：paths 不能为空");
            }

            if (rule.MinAgeDays is < 0)
            {
                problems.Add($"{label}：minAgeDays 不能是负数");
            }

            if (!Enum.IsDefined(rule.Risk))
            {
                problems.Add($"{label}：risk 只能是 low、medium 或 high");
            }
        }

        return problems;
    }
}
