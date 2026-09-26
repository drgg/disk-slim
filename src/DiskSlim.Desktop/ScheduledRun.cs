using DiskSlim.Core.History;
using DiskSlim.Core.Platform;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Scheduling;

namespace DiskSlim.Desktop;

/// <summary>
/// 定时任务启动 <c>DiskSlimApp.exe --scheduled-run</c> 时走这里：不显示任何窗口，清理完就退出。
/// DiskSlimApp.exe 是窗口程序，不会像 diskslim.exe 那样弹出黑色的控制台窗口。
/// </summary>
internal static class ScheduledRun
{
    /// <summary>退出码：0 成功，1 失败，2 定时清理已经关闭（什么也没做）。任务计划程序里能看到它。</summary>
    public static int Execute()
    {
        var manager = new ScheduleManager();

        // 设置文件被删除就表示用户关闭了定时清理：即使任务还在，也什么都不做。
        if (!manager.IsEnabled)
        {
            return 2;
        }

        var job = new ScheduledCleanupJob(RuleLoader.LoadDefault(), SystemInfo.SystemDriveRoot, manager, SnapshotStore.Default);
        return job.Run().Success ? 0 : 1;
    }
}
