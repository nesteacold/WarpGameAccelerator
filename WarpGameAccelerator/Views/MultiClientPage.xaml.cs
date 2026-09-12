// ============================================================
// Views/MultiClientPage.xaml.cs — Code-behind Multi-Client Launcher
// Mỏng: mọi logic nằm ở MultiClientViewModel — trang chỉ bind + chuyển tiếp
// sự kiện Click sang ViewModel (Tag của control luôn là đúng item x:Bind).
// ============================================================
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.ViewModels;

namespace WarpGameAccelerator.Views;

public sealed partial class MultiClientPage : Page
{
    public MultiClientViewModel ViewModel { get; }

    public MultiClientPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<MultiClientViewModel>();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo();
        ViewModel.RefreshRunning();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    // ── Quét tự động ─────────────────────────────────────────
    private async void DiscoverBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.DiscoverFoldersCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.DiscoverBtn_Click");
        }
    }

    // ── Thêm tay: Win32 Folder Browser Dialog (hoạt động trong unpackaged app) ──
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner      = ((App)Application.Current).MainWindowHandle,
                lpszTitle      = "Chọn thư mục cài đặt Age of Wushu",
                ulFlags        = 0x0001 | 0x0040, // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
                pszDisplayName = new string('\0', 260)
            };

            IntPtr pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return;

            var sb = new System.Text.StringBuilder(260);
            if (SHGetPathFromIDList(pidl, sb))
                ViewModel.AddFolder(sb.ToString());

            CoTaskMemFree(pidl);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.AddFolderBtn_Click");
        }
    }

    // ── Hàng thư mục: đếm số cửa sổ ───────────────────────────
    private void IncCount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) ViewModel.IncrementCount(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.IncCount_Click");
        }
    }

    private void DecCount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) ViewModel.DecrementCount(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.DecCount_Click");
        }
    }

    private void CountBox_LostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is TextBox { Tag: GameFolderRowViewModel row } tb &&
                int.TryParse(tb.Text, out int value))
            {
                ViewModel.SetCount(row, value);
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.CountBox_LostFocus");
        }
    }

    // ── Hàng thư mục: mở game / chevron actions ───────────────
    private async void LaunchSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        try
        {
            if (sender.Tag is GameFolderRowViewModel row)
                await ViewModel.LaunchCommand.ExecuteAsync(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.LaunchSplitButton_Click");
        }
    }

    private void HideAllRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) ViewModel.ToggleHideAll(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.HideAllRow_Click");
        }
    }

    private void StopAllRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) ViewModel.StopAll(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.StopAllRow_Click");
        }
    }

    private void RemoveFolderRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) ViewModel.RemoveFolder(row);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.RemoveFolderRow_Click");
        }
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetRow(sender) is { } row) row.IsExpanded = !row.IsExpanded;
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.ToggleExpand_Click");
        }
    }

    // ── Client đơn lẻ (trong hàng đã mở rộng, hoặc bucket "không rõ thư mục") ──
    private void HideOne_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetClient(sender) is { } client) ViewModel.ToggleHideOne(client);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.HideOne_Click");
        }
    }

    private void KillOne_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetClient(sender) is { } client) ViewModel.KillOne(client);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.KillOne_Click");
        }
    }

    // ── Helper: mọi control action đều mang item của nó trong Tag (x:Bind) ──
    private static GameFolderRowViewModel? GetRow(object sender) =>
        (sender as FrameworkElement)?.Tag as GameFolderRowViewModel;

    private static RunningClient? GetClient(object sender) =>
        (sender as FrameworkElement)?.Tag as RunningClient;
}
