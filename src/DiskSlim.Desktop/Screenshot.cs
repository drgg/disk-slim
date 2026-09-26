using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskSlim.Desktop;

/// <summary>
/// 截图模式的参数：<c>DiskSlimApp.exe --screenshot main.png [--tab 1] [--size 1280x820] [--navigate C:\Users] [--cleanup-dialog dialog.png]</c>。
/// </summary>
internal sealed record ScreenshotOptions(string Path, int Tab, int Width, int Height, string? NavigatePath, string? CleanupDialogPath)
{
    public static ScreenshotOptions? Parse(IReadOnlyList<string> args)
    {
        string? path = null, navigate = null, dialog = null;
        int tab = 0, width = 1280, height = 820;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            switch (args[i])
            {
                case "--screenshot":
                    path = args[++i];
                    break;
                case "--tab":
                    tab = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--size":
                    var parts = args[++i].Split('x');
                    width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--navigate":
                    navigate = args[++i];
                    break;
                case "--cleanup-dialog":
                    dialog = args[++i];
                    break;
            }
        }

        return path is null ? null : new ScreenshotOptions(System.IO.Path.GetFullPath(path), tab, width, height, navigate, dialog is null ? null : System.IO.Path.GetFullPath(dialog));
    }
}

/// <summary>
/// 截图模式：在屏幕外打开窗口、扫描、渲染成 PNG 后退出。用于检查界面和生成文档配图。
/// 清理对话框只做模拟运行，截图模式永远不会执行清理，也不会保存空间快照。
/// </summary>
internal static class Screenshot
{
    public static async Task RunAsync(ScreenshotOptions options)
    {
        var window = new MainWindow
        {
            ScanOnLoad = false,
            Width = options.Width,
            Height = options.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft - options.Width - 200,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        // 截图不留下任何记录：空间变化页只读取已有的快照。
        window.ViewModel.SaveHistory = false;
        await window.ViewModel.ScanAsync();
        window.SelectTab(options.Tab);
        if (options.Tab == 3)
        {
            // 重复文件页：查找一次（只读）。
            await window.ViewModel.Duplicates.SearchAsync();
        }
        else if (options.Tab == 4)
        {
            // 系统清理页：等估算完成（只读，不会运行任何任务）。
            await window.ViewModel.SystemTasks.RefreshEstimatesAsync();
        }
        else if (options.Tab == 7)
        {
            // 定时清理页：读取状态（只查询，不会开启或修改任务）。
            await window.ViewModel.Schedule.RefreshAsync();
        }

        if (options.NavigatePath is { } target && window.ViewModel.MapRoot?.FindDescendant(target) is { } node)
        {
            window.ViewModel.Navigate(node);
        }

        await WaitForIdle(window.Dispatcher);
        SavePng((FrameworkElement)window.Content, options.Path);

        if (options.CleanupDialogPath is { } dialogPath && window.ViewModel.CreateCleanup() is { } cleanup)
        {
            await cleanup.PrepareAsync(); // 只模拟，不执行
            var dialog = new CleanupWindow(cleanup)
            {
                PrepareOnLoad = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 1000,
                Top = 0,
                ShowActivated = false,
            };
            dialog.Show();
            await WaitForIdle(dialog.Dispatcher);
            SavePng((FrameworkElement)dialog.Content, dialogPath);
            dialog.Close();
        }

        window.Close();
    }

    private static async Task WaitForIdle(Dispatcher dispatcher)
    {
        for (var i = 0; i < 3; i++)
        {
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
        }
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
