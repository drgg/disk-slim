using System.Globalization;
using System.Xml.Linq;

namespace DiskSlim.Core.Scheduling;

/// <summary>
/// 生成“任务计划程序”的任务定义（XML）。任务以当前用户身份、普通权限运行，只在用户登录时运行，不需要保存密码。
/// </summary>
/// <remarks>
/// 条件：使用电池时不启动（运行中拔掉电源会停下）；错过的计划（关机、睡眠）在下次可用时补上；最长运行 1 小时；
/// 同一时间只运行一个实例。元素顺序和 Windows 导出的任务一致。
/// </remarks>
public static class ScheduledTaskDefinition
{
    public static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(ScheduleSettings settings, string userSid, DateTime now)
    {
        var start = settings.NextRun(now);
        var schedule = settings.Frequency == ScheduleFrequency.Daily
            ? new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1))
            : new XElement(Ns + "ScheduleByWeek",
                new XElement(Ns + "DaysOfWeek", new XElement(Ns + settings.Day.ToString())),
                new XElement(Ns + "WeeksInterval", 1));

        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "DiskSlim"),
                new XElement(Ns + "Description", $"DiskSlim 硬盘瘦身：{settings.Describe()} 清理“可放心清理”的缓存和临时文件。在 DiskSlim 里可以修改或关闭。")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "CalendarTrigger",
                    new XElement(Ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement(Ns + "Enabled", "true"),
                    schedule)),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "true"),
                new XElement(Ns + "StopIfGoingOnBatteries", "true"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT1H"),
                new XElement(Ns + "Priority", 7)),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", $"\"{settings.Program}\""),
                    new XElement(Ns + "Arguments", ScheduleManager.RunArgument),
                    new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(settings.Program)))));

        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task).Declaration + Environment.NewLine + task;
    }

    /// <summary>从任务定义里读出任务本身是否启用（用户可能在任务计划程序里手动禁用了它）。读不出来时返回 null。</summary>
    public static bool? IsEnabled(string xml)
    {
        try
        {
            var start = xml.IndexOf('<');
            var document = XDocument.Parse(start > 0 ? xml[start..] : xml);
            var value = document.Root?.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value;
            return value is null ? true : bool.Parse(value);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }
}
