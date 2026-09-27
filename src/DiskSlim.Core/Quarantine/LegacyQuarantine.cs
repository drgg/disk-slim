using System.Globalization;
using System.Text.Json;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.Quarantine;

/// <summary>旧版（脚本版）硬盘瘦身工具的隔离清单：<c>X:\.diskslim-quarantine\&lt;批次&gt;\manifest.json</c>。</summary>
internal sealed record LegacyManifest
{
    public string? Stamp { get; init; }

    /// <summary>通常是 “2026-09-09T20:34:13” 这样不带时区的本地时间；写成别的形式时退回用批次名判断。</summary>
    public JsonElement? CreatedAt { get; init; }

    public IReadOnlyList<LegacyManifestItem>? Items { get; init; }
}

internal sealed record LegacyManifestItem
{
    public string? Original { get; init; }

    public string? Stored { get; init; }

    public long? Bytes { get; init; }
}

/// <summary>
/// 读取旧版工具留下的隔离批次，让它们和现在的批次一样可以列出、恢复、彻底删除。
/// </summary>
/// <remarks>
/// 旧版的一项可能是整个文件夹（例如 <c>0001_codex-runtimes</c>），恢复时整个文件夹移回原位。
/// 旧版清单不再改写：恢复了的项目在批次目录里就不存在了，下次读取时自然不再列出。
/// </remarks>
internal static class LegacyQuarantine
{
    public const string DirectoryName = ".diskslim-quarantine";
    public const string ManifestFileName = "manifest.json";

    public static QuarantineSession? Load(string directory)
    {
        LegacyManifest? manifest;
        try
        {
            var path = Path.Join(directory, ManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            manifest = JsonSerializer.Deserialize(File.ReadAllText(path), DiskSlimJsonContext.Default.LegacyManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (manifest?.Items is not { } entries)
        {
            return null;
        }

        var items = new List<QuarantineItem>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            // 存放的名字必须是这一批目录里的直接子项、原位置必须是完整路径：清单被改坏时不会指向别处。
            if (entry.Stored is not { Length: > 0 } stored || stored is "." or ".." || stored.IndexOfAny(['\\', '/', ':']) >= 0
                || entry.Original is not { } original || !Path.IsPathFullyQualified(original))
            {
                continue;
            }

            var storedPath = Path.Join(directory, stored);
            var isDirectory = Directory.Exists(storedPath);
            if (!isDirectory && !File.Exists(storedPath))
            {
                continue; // 已经恢复或删除了
            }

            var bytes = entry.Bytes ?? (isDirectory ? MeasureDirectory(storedPath) : new FileInfo(storedPath).Length);
            items.Add(new QuarantineItem(i + 1, Path.GetFullPath(original), storedPath, bytes, Rule: null) { IsDirectory = isDirectory });
        }

        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return new QuarantineSession(id, directory, CreatedAt(manifest, id, directory), Describe(items), items) { IsLegacy = true };
    }

    private static DateTimeOffset CreatedAt(LegacyManifest manifest, string id, string directory)
    {
        if (manifest.CreatedAt is { ValueKind: JsonValueKind.String } element
            && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created))
        {
            return created;
        }

        if (DateTime.TryParseExact(manifest.Stamp ?? id, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp))
        {
            return new DateTimeOffset(stamp);
        }

        return new DateTimeOffset(Directory.GetCreationTime(directory));
    }

    /// <summary>例如 “旧版工具移入的 184 项：codex-runtimes、puppeteer、cache 等（含 48 个文件夹）”。</summary>
    private static string Describe(IReadOnlyList<QuarantineItem> items)
    {
        var largest = items.OrderByDescending(i => i.Bytes).Take(3).Select(i => Path.GetFileName(i.OriginalPath)).ToList();
        var folders = items.Count(i => i.IsDirectory);
        return $"旧版工具移入的 {items.Count:N0} 项"
            + (largest.Count > 0 ? "：" + string.Join("、", largest) + (items.Count > largest.Count ? " 等" : "") : "")
            + (folders > 0 ? $"（含 {folders:N0} 个文件夹）" : "");
    }

    private static long MeasureDirectory(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
