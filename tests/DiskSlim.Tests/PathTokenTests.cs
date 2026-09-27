using System.Diagnostics;
using System.Text;
using DiskSlim.Core.Cleaning;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;

namespace DiskSlim.Tests;

/// <summary>
/// &lt;drives&gt;、&lt;steam-libraries&gt;、&lt;app:xxx.exe&gt; 这类路径前缀，以及只处理旧版本程序文件夹的 oldVersionsOnly。
/// 测试用假的前缀解析，不读这台电脑的注册表和磁盘列表。
/// </summary>
public sealed class PathTokenTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-30);
    private readonly TestDirectory _dir = new();
    private readonly FakeTokens _tokens = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Drive_token_expands_on_every_drive()
    {
        _dir.CreateDirectory("D/BaiduNetdiskDownload");
        _dir.CreateDirectory("E/BaiduNetdiskDownload");
        _dir.CreateDirectory("F");
        _tokens.Map["drives"] = [_dir.PathOf("D"), _dir.PathOf("E"), _dir.PathOf("F")];

        var paths = PathExpander.Expand("<drives>/BaiduNetdiskDownload", _tokens);

        Assert.Equal([_dir.PathOf(@"D\BaiduNetdiskDownload"), _dir.PathOf(@"E\BaiduNetdiskDownload")], paths);
    }

    [Fact]
    public void Tokens_combine_with_wildcards()
    {
        _dir.CreateDirectory("lib1/steamapps/shadercache");
        _dir.CreateDirectory("lib2/steamapps/downloading");
        _tokens.Map["steam-libraries"] = [_dir.PathOf("lib1"), _dir.PathOf("lib2")];

        var paths = PathExpander.Expand("<steam-libraries>/steamapps/*", _tokens);

        Assert.Equal(2, paths.Count);
    }

    [Theory]
    [InlineData("<nope>/x")]
    [InlineData("<app:quark>/x")]        // 必须是 .exe
    [InlineData("<app:..\\evil.exe>/x")] // 不能带路径
    public void Unknown_tokens_expand_to_nothing(string pattern)
    {
        Assert.Empty(PathExpander.Expand(pattern, _tokens));
    }

    [Theory]
    [InlineData("<drives>", true)]
    [InlineData("<steam-libraries>", true)]
    [InlineData("<app:quark.exe>", true)]
    [InlineData("<nope>", false)]
    public void Rule_files_reject_unknown_tokens(string prefix, bool valid)
    {
        var json = $$"""{ "rules": [ { "id": "a", "name": "A", "paths": ["{{prefix}}/x"], "risk": "high" } ] }""";

        var load = () => RuleLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "测试");

        if (valid)
        {
            load();
        }
        else
        {
            Assert.Contains("不认识路径前缀", Assert.Throws<InvalidDataException>(load).Message);
        }
    }

    [Fact]
    public void Old_versions_only_requires_a_wildcard_last_segment()
    {
        var json = """{ "rules": [ { "id": "a", "name": "A", "paths": ["<app:quark.exe>/7.1.7.975"], "risk": "medium", "action": "quarantine", "oldVersionsOnly": true } ] }""";

        var ex = Assert.Throws<InvalidDataException>(() => RuleLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "测试"));

        Assert.Contains("oldVersionsOnly", ex.Message);
    }

    [Fact]
    public void Old_versions_keep_the_newest_and_whatever_the_program_itself_uses()
    {
        // 真实情况：quark.exe 还是 6.9.2.881（新版的 new_quark.exe 还没换上去），所以最旧的那个反而在用。
        var app = _dir.CreateDirectory("Quark");
        var exe = Path.Join(app, "quark.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
        var exeVersion = FileVersionInfo.GetVersionInfo(exe);
        var inUse = $"{exeVersion.FileMajorPart}.{exeVersion.FileMinorPart}.{exeVersion.FileBuildPart}.{exeVersion.FilePrivatePart}";
        foreach (var version in new[] { inUse, "1.2.3.4", "900.0.0.1" })
        {
            _dir.WriteFile($"Quark/{version}/app.dll", 100);
        }

        _dir.CreateDirectory("Quark/Dictionaries"); // 不是版本号，不动

        var old = RuleMatcher.OldVersions(PathExpander.Expand(app + "/*"));

        Assert.Equal([Path.Join(app, "1.2.3.4")], old);
    }

    [Fact]
    public void A_single_version_is_never_old()
    {
        _dir.WriteFile("App/1.0.0/app.dll", 100);

        Assert.Empty(RuleMatcher.OldVersions([_dir.PathOf("App/1.0.0")]));
    }

    [Fact]
    public void Cleaning_old_versions_removes_the_emptied_version_folder_too()
    {
        _dir.WriteFile("Quark/1.0.0.1/app.dll", 100, Old);
        _dir.WriteFile("Quark/1.0.0.1/locales/zh.pak", 50, Old);
        _dir.SetDirectoryTime("Quark/1.0.0.1/locales", Old);
        _dir.WriteFile("Quark/2.0.0.1/app.dll", 100, Old);
        _tokens.Map["app:quark.exe"] = [_dir.PathOf("Quark")];
        var rule = new CleanupRule
        {
            Id = "quark-old-versions", Name = "旧版本", Risk = RiskLevel.Medium, Action = CleanupAction.Quarantine,
            Paths = ["<app:quark.exe>/*.*.*.*"], OldVersionsOnly = true,
        };
        var drive = Path.GetPathRoot(_dir.Root)!;
        var store = new Core.Quarantine.QuarantineStore(_dir.PathOf("quarantine"));

        var targets = new RuleMatcher(drive, _tokens).Match(rule);
        Assert.Equal([_dir.PathOf("Quark/1.0.0.1")], targets.Select(t => t.Path));

        var plan = new CleanupPlan
        {
            DriveRoot = drive,
            Rules = [new PlannedRule { Rule = rule, Status = PlanStatus.Ready, Targets = targets, ExplicitlySelected = true }],
            Available = [],
        };
        var result = new CleanupExecutor(quarantine: store).Execute(plan, dryRun: false);

        Assert.Equal(150, result.QuarantinedBytes);
        Assert.False(Directory.Exists(_dir.PathOf("Quark/1.0.0.1")));
        Assert.True(File.Exists(_dir.PathOf("Quark/2.0.0.1/app.dll")));

        // 可以完整恢复，包括文件夹本身。
        store.Restore(store.Find(result.QuarantineSessionId!)!, SafetyGuard.ForCurrentUser());
        Assert.True(File.Exists(_dir.PathOf("Quark/1.0.0.1/locales/zh.pak")));
    }

    [Fact]
    public void Steam_library_folders_are_read_from_libraryfolders_vdf()
    {
        var vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam"  "label" "" }
                "1" { "path"  "D:\\SteamLibrary" }
                "2" { "path"		"E:\\SteamLibrary" }
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\SteamLibrary"], InstalledApps.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void Default_rules_find_things_on_other_drives()
    {
        // 这台电脑上 D 盘 98% 满，而旧规则只认 %LOCALAPPDATA% 这类环境变量，在 D、E 盘一条都找不到。
        var rules = RuleLoader.LoadDefault().Rules;

        Assert.Contains(rules, r => r.Paths.Any(p => p.StartsWith("<drives>", StringComparison.Ordinal)));
        Assert.Contains(rules, r => r.Paths.Any(p => p.StartsWith("<steam-libraries>", StringComparison.Ordinal)));
        Assert.Contains(rules, r => r.OldVersionsOnly && r.Action == CleanupAction.Quarantine);
        Assert.Contains("<drives>/pagefile.sys", rules.Single(r => r.Id == "page-file").Paths);
    }

    private sealed class FakeTokens : IPathTokenSource
    {
        public Dictionary<string, IReadOnlyList<string>> Map { get; } = [];

        public IReadOnlyList<string> Resolve(string token) => Map.TryGetValue(token, out var paths) ? paths : [];
    }
}
