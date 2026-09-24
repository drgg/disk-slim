using DiskSlim.Cli;

namespace DiskSlim.Tests;

/// <summary>端到端：通过命令行入口运行 diskslim clean，规则只指向临时目录。</summary>
public sealed class CleanCommandTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly string _rulesFile;

    public CleanCommandTests()
    {
        _dir.WriteFile("cache/junk.tmp", 4096, Old);
        _dir.WriteFile("cache/new.tmp", 10);
        _dir.WriteFile("big-cache/model.bin", 8192, Old);
        _dir.WriteFile("chat/history.db", 100, Old);

        var root = _dir.Root.Replace('\\', '/');
        _rulesFile = _dir.PathOf("rules.json");
        File.WriteAllText(_rulesFile, $$"""
            {
              "rules": [
                { "id": "test-chat", "name": "测试聊天记录", "paths": ["{{root}}/chat"], "risk": "high" },
                { "id": "test-cache", "name": "测试缓存", "paths": ["{{root}}/cache"], "risk": "low", "action": "delete", "minAgeDays": 1 },
                { "id": "test-big", "name": "测试大缓存", "paths": ["{{root}}/big-cache"], "risk": "medium", "action": "delete" }
              ]
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    private (int ExitCode, string Output, string Errors) Run(params string[] args) => RunWithInput(null, args);

    /// <param name="typed">用户在确认提示时输入的内容；为 null 表示非交互环境。</param>
    private (int ExitCode, string Output, string Errors) RunWithInput(string? typed, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = CliApp.Run(["clean", "--rules", _rulesFile, "--no-color", .. args], stdout, stderr,
            new StringReader(typed ?? ""), interactive: typed is not null);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Typing_y_at_the_prompt_executes()
    {
        var (exitCode, output, _) = RunWithInput("y\n", "--execute", "--log", _dir.PathOf("logs/confirmed.jsonl"));

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("确定执行吗", output);
        Assert.False(File.Exists(_dir.PathOf("cache/junk.tmp")));
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("yes please\n")]
    public void Anything_but_y_at_the_prompt_cancels(string typed)
    {
        var (exitCode, output, _) = RunWithInput(typed, "--execute", "--log", _dir.PathOf("logs/declined.jsonl"));

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("已取消，没有删除任何文件", output);
        Assert.True(File.Exists(_dir.PathOf("cache/junk.tmp")));
        Assert.False(File.Exists(_dir.PathOf("logs/declined.jsonl")));
    }

    [Fact]
    public void Dry_run_shows_the_plan_and_deletes_nothing()
    {
        var (exitCode, output, _) = Run();

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("模拟运行", output);
        Assert.Contains("测试缓存", output);
        Assert.Contains("永久删除", output);
        Assert.Contains("test-big", output); // “需要确认”的项目列为可点名
        Assert.Contains("--execute", output);
        Assert.True(File.Exists(_dir.PathOf("cache/junk.tmp")));
    }

    [Fact]
    public void Execute_with_yes_cleans_and_writes_a_log()
    {
        var logPath = _dir.PathOf("logs/run.jsonl");

        var (exitCode, output, errors) = Run("--execute", "--yes", "--log", logPath);

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Equal("", errors);
        Assert.False(File.Exists(_dir.PathOf("cache/junk.tmp")));
        Assert.True(File.Exists(_dir.PathOf("cache/new.tmp")));      // 太新
        Assert.True(File.Exists(_dir.PathOf("big-cache/model.bin"))); // 需要确认，没有点名
        Assert.True(File.Exists(_dir.PathOf("chat/history.db")));     // 仅供参考
        Assert.Contains("已删除 1 个文件", output);
        Assert.Contains(logPath, output);
        Assert.Contains("\"event\":\"deleted\"", File.ReadAllText(logPath));
    }

    [Fact]
    public void Medium_rules_are_cleaned_only_when_named()
    {
        var (exitCode, _, _) = Run("--rule", "test-big", "--execute", "--yes", "--log", _dir.PathOf("logs/big.jsonl"));

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.False(File.Exists(_dir.PathOf("big-cache/model.bin")));
        Assert.True(File.Exists(_dir.PathOf("cache/junk.tmp"))); // 点名后只清理点名的项目
    }

    [Fact]
    public void Refuses_to_clean_high_risk_rules()
    {
        var (exitCode, _, errors) = Run("--rule", "test-chat", "--execute", "--yes");

        Assert.Equal(CliApp.ExitUsage, exitCode);
        Assert.Contains("仅供参考", errors);
        Assert.True(File.Exists(_dir.PathOf("chat/history.db")));
    }

    [Fact]
    public void Execute_without_confirmation_in_a_non_interactive_shell_does_nothing()
    {
        var (exitCode, _, errors) = Run("--execute");

        Assert.Equal(CliApp.ExitOk, exitCode);
        Assert.Contains("--yes", errors);
        Assert.True(File.Exists(_dir.PathOf("cache/junk.tmp")));
    }
}
