using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiskSlim.Core.Serialization;

namespace DiskSlim.Core.SystemTasks;

/// <summary>
/// 以管理员身份启动 diskslim.exe 作为助手进程执行一项系统任务（会弹出 UAC 提示），等它结束后读回结果。
/// </summary>
/// <remarks>
/// 助手进程只接受 <see cref="SystemTaskCatalog"/> 里的任务 id；结果文件只能写在 DiskSlim 自己的助手目录里，
/// 以免管理员权限的进程被诱导往任意位置写文件。
/// </remarks>
public static class ElevatedHelper
{
    private const int UserCancelledUac = 1223;

    public static string HelperDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSlim", "helper");

    /// <summary>结果文件必须是助手目录里的 .json 文件。</summary>
    public static bool IsValidResultPath(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(HelperDirectory), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>以管理员身份运行任务。用户在 UAC 提示里点了“否”时返回 null。</summary>
    public static async Task<SystemTaskResult?> RunAsync(string helperExecutable, string taskId, string logPath)
    {
        if (SystemTaskCatalog.Find(taskId) is null)
        {
            throw new ArgumentException($"没有这项系统任务：{taskId}", nameof(taskId));
        }

        Directory.CreateDirectory(HelperDirectory);
        var resultPath = Path.Combine(HelperDirectory, $"result-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo(helperExecutable, QuoteArguments(
            ["system", "run", taskId, "--execute", "--yes", "--no-color", "--result-file", resultPath, "--log", logPath]))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        int exitCode;
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动管理员助手");
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledUac)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath), DiskSlimJsonContext.Default.SystemTaskResult)
                ?? Missing(taskId, exitCode);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return Missing(taskId, exitCode);
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    public static void WriteResult(string path, SystemTaskResult result)
    {
        if (!IsValidResultPath(path))
        {
            throw new ArgumentException($"结果文件只能位于 {HelperDirectory}", nameof(path));
        }

        File.WriteAllText(path, JsonSerializer.Serialize(result, DiskSlimJsonContext.Default.SystemTaskResult), new UTF8Encoding(false));
    }

    /// <summary>按 Windows 命令行规则给参数加引号（CommandLineToArgvW 能原样解析回来）。</summary>
    public static string QuoteArguments(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // 引号前的反斜杠要加倍，引号本身要转义。
            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            builder.Append(c);
            backslashes = 0;
        }

        // 结尾的反斜杠后面紧跟收尾引号，也要加倍。
        builder.Append('\\', backslashes * 2);
        return builder.Append('"').ToString();
    }

    private static SystemTaskResult Missing(string taskId, int exitCode) => new()
    {
        TaskId = taskId,
        Success = false,
        Message = $"管理员助手没有返回结果（退出码 {exitCode}）。如果你用的是另一个管理员账户授权的，结果会写到那个账户下，请改用当前账户以管理员身份运行。",
    };
}
