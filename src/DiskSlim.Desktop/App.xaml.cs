using System.Windows;
using System.Windows.Threading;

namespace DiskSlim.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshot = ScreenshotOptions.Parse(e.Args);
        DispatcherUnhandledException += (_, args) => OnUnhandledException(args, screenshot);

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

    private static void OnUnhandledException(DispatcherUnhandledExceptionEventArgs args, ScreenshotOptions? screenshot)
    {
        args.Handled = true;
        if (screenshot is not null)
        {
            File.WriteAllText(screenshot.Path + ".error.txt", args.Exception.ToString());
            Current.Shutdown(1);
            return;
        }

        MessageBox.Show($"出错了：{args.Exception.Message}", "DiskSlim 硬盘瘦身", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
