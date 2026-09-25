using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.Cleaning;

/// <summary>操作日志中的一条记录（JSON Lines，一行一条）。</summary>
public sealed record LogEntry
{
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// run-start、rule-start、deleted、recycled、quarantined、removed-dir、emptied-recycle-bin、purged-quarantine、failed、rule-end、run-end。
    /// </summary>
    public required string Event { get; init; }

    public string? Rule { get; init; }

    public string? Path { get; init; }

    public long? Bytes { get; init; }

    public string? Detail { get; init; }
}

public interface IOperationLog : IDisposable
{
    /// <summary>日志文件路径；不写文件时为 null。</summary>
    string? FilePath { get; }

    void Write(LogEntry entry);
}

/// <summary>不记录任何内容（模拟运行时使用）。</summary>
public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();

    public string? FilePath => null;

    public void Write(LogEntry entry)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>把操作逐条写入 JSON Lines 文件。每条记录写完就交给操作系统，程序中途崩溃也能留下记录。</summary>
public sealed class JsonLinesOperationLog : IOperationLog
{
    private static readonly JsonSerializerOptions Options = new(DiskSlimJsonContext.Default.Options)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public JsonLinesOperationLog(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // bufferSize: 1 关闭 FileStream 自己的缓冲，每一行都立即交给操作系统。
        var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    public string? FilePath { get; }

    /// <summary>默认位置：%LOCALAPPDATA%\DiskSlim\logs\clean-时间.jsonl。</summary>
    public static string DefaultPath(DateTimeOffset now) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSlim",
        "logs",
        $"clean-{now:yyyyMMdd-HHmmss}.jsonl");

    public void Write(LogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Options.GetTypeInfo(typeof(LogEntry)));
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
