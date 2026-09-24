using System.ComponentModel;
using System.Windows;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Desktop;

public partial class CleanupWindow : Window
{
    private readonly CleanupViewModel _viewModel;

    public CleanupWindow(CleanupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>对话框打开后开始模拟运行；截图模式会自己调用。</summary>
    public bool PrepareOnLoad { get; init; } = true;

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (PrepareOnLoad && _viewModel.Stage == CleanupStage.Preparing)
        {
            await _viewModel.PrepareAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 清理进行中不能直接关掉窗口：先请求停止，等当前文件处理完、日志写好。
        if (_viewModel.Stage == CleanupStage.Running)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
