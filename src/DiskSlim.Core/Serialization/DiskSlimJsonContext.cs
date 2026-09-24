using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSlim.Core.Reporting;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(RuleSet))]
[JsonSerializable(typeof(ScanReport))]
[JsonSerializable(typeof(Cleaning.LogEntry))]
internal sealed partial class DiskSlimJsonContext : JsonSerializerContext;

public static class ReportJson
{
    private static readonly JsonSerializerOptions Options = new(DiskSlimJsonContext.Default.Options)
    {
        // 中文直接输出，不转成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(ScanReport report, Stream stream) =>
        JsonSerializer.Serialize(stream, report, Options.GetTypeInfo(typeof(ScanReport)));

    public static string Serialize(ScanReport report) =>
        JsonSerializer.Serialize(report, Options.GetTypeInfo(typeof(ScanReport)));
}
