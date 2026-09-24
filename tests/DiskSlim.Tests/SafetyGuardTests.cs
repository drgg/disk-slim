using DiskSlim.Core.Cleaning;

namespace DiskSlim.Tests;

public sealed class SafetyGuardTests
{
    private static readonly SafetyGuard Guard = new(
        criticalFolders: [@"C:\Users\me", @"C:\Users\me\AppData\Local", @"C:\Windows"],
        forbiddenZones: [@"C:\Windows\System32", @"C:\Program Files"]);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users\me")] // 关键目录本身
    [InlineData(@"C:\Users\ME\")] // 大小写、结尾分隔符
    [InlineData(@"C:\Users")] // 关键目录的上级
    [InlineData(@"C:\Users\me\AppData")] // 包含 AppData\Local
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers")] // 禁区里面
    [InlineData(@"C:\Program Files\App\cache")]
    public void Refuses_protected_locations(string path)
    {
        Assert.NotNull(Guard.Check(path));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\Users\me\AppData\Local\NetEase\CloudMusic\Cache")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Users\me\Downloads\setup.exe")]
    [InlineData(@"C:\Program Files Extra\cache")] // 只是名字相似
    public void Allows_specific_folders_inside_critical_ones(string path)
    {
        Assert.Null(Guard.Check(path));
    }

    [Fact]
    public void Current_user_guard_protects_real_system_and_user_folders()
    {
        var guard = SafetyGuard.ForCurrentUser();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Null(guard.Check(Path.GetTempPath()));
        Assert.Null(guard.Check(Path.Combine(local, "npm-cache")));

        string[] protectedPaths =
        [
            profile,
            local,
            roaming,
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS", "Temp"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Path.Combine(roaming, "Microsoft"), // 包含凭据目录
            Path.Combine(profile, ".ssh", "known_hosts"),
        ];
        Assert.All(protectedPaths, p => Assert.NotNull(guard.Check(p)));
    }
}
