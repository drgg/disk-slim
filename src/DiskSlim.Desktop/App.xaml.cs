using System.Windows;
using System.Windows.Threading;
using DiskSlim.Core.Scheduling;

namespace DiskSlim.Desktop;

public partial class App : Application
{
    private bool _headless;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshot = ScreenshotOptions.Parse(e.Args);
        DispatcherUnhandledException += (_, args) => OnUnhandledException(args, screenshot);

        if (e.Args.Contains(ScheduleManager.RunArgument))
        {
            // 定时清理：没有窗口，也不弹任何对话框。
            _headless = true;
            int exitCode;
            try
            {
                exitCode = await Task.Run(ScheduledRun.Execute);
            }
            catch (Exception)
            {
                exitCode = 1;
            }

            Shutdown(exitCode);
            return;
        }

        if (screenshot is null)
        {
            new MainWindow().Show();
            return;
        }

        try
        {
            await Screenshot.RunAsync(screenshot);
        }
        catch (Exception ex)
        {
            File.WriteAllText(screenshot.Path + ".error.txt", ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private void OnUnhandledException(DispatcherUnhandledExceptionEventArgs args, ScreenshotOptions? screenshot)
    {
        args.Handled = true;
        if (_headless)
        {
            Current.Shutdown(1);
            return;
        }

        if (screenshot is not null)
        {
            File.WriteAllText(screenshot.Path + ".error.txt", args.Exception.ToString());
            Current.Shutdown(1);
            return;
        }

        MessageBox.Show($"出错了：{args.Exception.Message}", "DiskSlim 硬盘瘦身", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
