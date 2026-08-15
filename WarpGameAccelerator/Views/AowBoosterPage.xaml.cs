// ============================================================
// Views/AowBoosterPage.xaml.cs — Code-behind AoW Booster
// ============================================================
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Views;

public sealed partial class AowBoosterPage : Page
{
    private string _gameFolder = string.Empty;

    public AowBoosterPage()
    {
        InitializeComponent();
        _ = LoadSavedStateAsync();
    }

    private async Task LoadSavedStateAsync()
    {
        try
        {
            // Ưu tiên thư mục đã lưu riêng cho AoW Booster; nếu chưa có, mượn
            // tạm thư mục đã cấu hình ở Multi-Client (cùng là thư mục gốc AOW).
            var folder = await DxvkBoosterService.LoadSavedFolderAsync();
            if (string.IsNullOrEmpty(folder))
                folder = MultiClientService.LoadToken()?.GameFolder;

            if (!string.IsNullOrEmpty(folder))
            {
                FolderPathBox.Text = folder;
                ValidateFolder(folder);
            }

            NvOverlayToggle.IsOn = NvOverlayOptimizerService.IsApplied();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.LoadSavedStateAsync");
        }
    }

    private async void NvOverlayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (NvOverlayToggle.IsOn)
                await NvOverlayOptimizerService.ApplyAsync();
            else
                await NvOverlayOptimizerService.RestoreAsync();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.NvOverlayToggle_Toggled");
        }
    }

    // ── Win32 Folder Browser Dialog (giống MultiClientPage) ──
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

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
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
                FolderPathBox.Text = sb.ToString();

            CoTaskMemFree(pidl);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.BrowseBtn_Click");
        }
    }

    private void FolderPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateFolder(FolderPathBox.Text.Trim());
    }

    private async void ValidateFolder(string folder)
    {
        // async void: mọi exception thoát ra khỏi đây sẽ giết process ngay
        // lập tức mà không handler nào bắt được → bắt buộc bọc try/catch.
        try
        {
            _gameFolder = folder;
            var (valid, msg) = DxvkBoosterService.ValidateGameFolder(folder);
            FolderStatusText.Text = msg;
            FolderStatusText.Foreground = valid
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136));

            InstallBtn.IsEnabled     = valid;
            UninstallBtn.IsEnabled   = valid;
            CleanLogsBtn.IsEnabled   = valid;

            if (valid)
            {
                await DxvkBoosterService.SaveFolderAsync(_gameFolder);
                RefreshStatus();
            }
            else
            {
                SetStatus(installed: null, subtitle: msg);
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.ValidateFolder");
        }
    }

    private void RefreshStatus()
    {
        try
        {
            bool installed = DxvkBoosterService.IsInstalled(_gameFolder);
            SetStatus(installed, installed ? "bin64\\d3d9.dll" : "Bấm \"Cài đặt lại\" để kích hoạt");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.RefreshStatus");
        }
    }

    private void SetStatus(bool? installed, string subtitle)
    {
        StatusSubtitle.Text = subtitle;

        if (installed == true)
        {
            StatusIcon.Text = "⚡";
            StatusTitle.Text = "Đã cài đặt";
            StatusBadge.Visibility = Visibility.Visible;
            StatusBadgeText.Text = "Đang hoạt động";
        }
        else if (installed == false)
        {
            StatusIcon.Text = "○";
            StatusTitle.Text = "Chưa cài đặt";
            StatusBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusIcon.Text = "?";
            StatusTitle.Text = "Chưa xác định";
            StatusBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void SetBusy(bool busy, string progressText = "")
    {
        InstallBtn.IsEnabled   = !busy && DxvkBoosterService.ValidateGameFolder(_gameFolder).Valid;
        UninstallBtn.IsEnabled = InstallBtn.IsEnabled;
        CleanLogsBtn.IsEnabled = InstallBtn.IsEnabled;
        ProgressText.Text = progressText;
    }

    // Chạy 1 hành động của launcher; nếu thất bại do thiếu .NET Runtime, tự
    // tải + cài silent rồi thử lại đúng 1 lần — người dùng chỉ cần bấm 1 nút,
    // không phải tự đi cài .NET rồi bấm lại.
    private async Task<(bool Success, string Output)> RunWithRuntimeAutoInstallAsync(
        Func<Task<(bool Success, string Output)>> action, string actionLabel)
    {
        var (ok, output) = await action();
        if (ok || !DxvkBoosterService.IsMissingRuntimeError(output))
            return (ok, output);

        SetBusy(true, "Chưa có .NET Runtime trên máy — đang tự tải & cài (im lặng)...");
        var (runtimeOk, runtimeMsg) = await DxvkBoosterService.InstallDotNetRuntimeSilentlyAsync();
        DiagnosticLogService.Trace($"[AowBooster] Auto-install .NET Runtime ok={runtimeOk}: {runtimeMsg}");
        if (!runtimeOk)
            return (false, $"Không cài được .NET Runtime tự động: {runtimeMsg}");

        SetBusy(true, $"Đã cài .NET Runtime — đang thử {actionLabel} lại...");
        return await action();
    }

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Đang triển khai AoW Booster...");
            var (ok, output) = await RunWithRuntimeAutoInstallAsync(
                () => DxvkBoosterService.InstallAsync(_gameFolder), "cài đặt");
            DiagnosticLogService.Trace($"[AowBooster] Install ok={ok}\n{output}");
            SetBusy(false, ok ? "Cài đặt xong." : $"Cài đặt thất bại: {Tail(output)}");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.InstallBtn_Click");
            SetBusy(false, $"Có lỗi xảy ra khi cài đặt: {ex.Message}");
        }
    }

    private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Đang gỡ cài đặt, khôi phục file gốc...");
            var (ok, output) = await RunWithRuntimeAutoInstallAsync(
                () => DxvkBoosterService.UninstallAsync(_gameFolder), "gỡ cài đặt");
            DiagnosticLogService.Trace($"[AowBooster] Uninstall ok={ok}\n{output}");
            SetBusy(false, ok ? "Đã gỡ cài đặt." : $"Gỡ cài đặt thất bại: {Tail(output)}");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.UninstallBtn_Click");
            SetBusy(false, $"Có lỗi xảy ra khi gỡ cài đặt: {ex.Message}");
        }
    }

    private async void CleanLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Đang dọn log rác...");
            var (ok, output) = await RunWithRuntimeAutoInstallAsync(
                () => DxvkBoosterService.CleanLogsAsync(_gameFolder), "dọn log");
            DiagnosticLogService.Trace($"[AowBooster] CleanLogs ok={ok}\n{output}");
            SetBusy(false, ok ? "Đã dọn log rác." : $"Dọn log thất bại: {Tail(output)}");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "AowBoosterPage.CleanLogsBtn_Click");
            SetBusy(false, $"Có lỗi xảy ra khi dọn log: {ex.Message}");
        }
    }

    // Cắt output của launcher (nhiều dòng, có cả banner menu) xuống phần cuối
    // cùng, ngắn gọn đủ hiện trực tiếp trên UI mà không cần mở file log.
    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var relevant = lines.Length > 4 ? lines[^4..] : lines;
        return string.Join(" · ", relevant);
    }
}
