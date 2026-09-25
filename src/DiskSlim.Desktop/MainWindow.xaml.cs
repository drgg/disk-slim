using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DiskSlim.Core.Rules;
using DiskSlim.Core.Visualization;
using DiskSlim.Desktop.ViewModels;

namespace DiskSlim.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainViewModel(RuleLoader.LoadDefault()))
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        viewModel.CleanupRequested += OnCleanupRequested;
        viewModel.RestartAsAdminRequested += OnRestartAsAdmin;
        viewModel.Quarantine.ConfirmPurge = question =>
            MessageBox.Show(this, question, "彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        viewModel.Duplicates.Confirm = question =>
            MessageBox.Show(this, question, "移入隔离区", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        viewModel.Duplicates.PickFolder = initial =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要查找重复文件的位置", InitialDirectory = Directory.Exists(initial) ? initial : "" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        Treemap.TileClicked += (_, e) =>
        {
            if (e.Tile is { Kind: TileKind.Directory, Node: { } node })
            {
                viewModel.Navigate(node);
            }
        };
        Treemap.HoveredTileChanged += (_, e) => viewModel.DescribeTile(e.Tile);
    }

    public MainViewModel ViewModel { get; }

    /// <summary>窗口打开后自动开始的第一次扫描。</summary>
    public Task? InitialScan { get; private set; }

    /// <summary>为 false 时窗口打开后不自动扫描（截图模式自己控制）。</summary>
    public bool ScanOnLoad { get; init; } = true;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (ScanOnLoad && InitialScan is null)
        {
            InitialScan = ViewModel.ScanAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ViewModel.CancelScanCommand.Execute(null);
        base.OnClosing(e);
    }

    /// <summary>切换到指定标签页（0 = 可清理项目，1 = 空间分布，2 = 最大的文件，3 = 重复文件，4 = 隔离区）。</summary>
    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    private async void OnCleanupRequested(object? sender, CleanupViewModel cleanup)
    {
        var dialog = new CleanupWindow(cleanup) { Owner = this };
        dialog.ShowDialog();
        if (cleanup.HasExecuted)
        {
            // 清理过后重新扫描，让列表和方块图反映最新情况。
            await ViewModel.ScanAsync();
        }
    }

    private void OnRestartAsAdmin(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 提示里点了“否”。
        }
    }

    private void OnTreemapShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (Treemap.ContextTile is { } tile)
        {
            ShowInExplorer(tile.Path);
        }
    }

    private void OnChildDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedChild();

    private void OnChildOpen(object sender, RoutedEventArgs e) => OpenSelectedChild();

    private void OnChildShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (ChildrenList.SelectedItem is FolderItemViewModel item)
        {
            ShowInExplorer(item.Path);
        }
    }

    private void OnLargeFileShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (LargestFilesList.SelectedItem is LargeFileViewModel file)
        {
            ShowInExplorer(file.Path);
        }
    }

    private void OnDuplicateShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DuplicateFileViewModel file })
        {
            ShowInExplorer(file.Path);
        }
    }

    private void OpenSelectedChild()
    {
        if (ChildrenList.SelectedItem is FolderItemViewModel { Node: { } node })
        {
            ViewModel.Navigate(node);
        }
    }

    /// <summary>在资源管理器中选中这个文件或文件夹（只打开窗口，不做任何修改）。</summary>
    private static void ShowInExplorer(string path)
    {
        var arguments = File.Exists(path) || Directory.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
