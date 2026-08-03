// ============================================================
// Views/WarpAccountPage.xaml.cs — Code-behind cho màn hình WARP+ Account
// ============================================================
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Views;

public sealed partial class WarpAccountPage : Page
{
    private readonly LocalizationService _loc;

    public WarpAccountPage()
    {
        InitializeComponent();
        _loc = App.Services.GetRequiredService<LocalizationService>();
        _ = LoadAccountInfoAsync();
    }

    private async Task LoadAccountInfoAsync()
    {
        var info = await WarpAccountService.GetOrCreateAccountAsync();
        if (!string.IsNullOrEmpty(info.Id))
        {
            AccountIdText.Text = $"ID: {info.Id[..Math.Min(16, info.Id.Length)]}...";
        }

        // Hiển thị trạng thái tài khoản — KHÔNG dựa vào info.License có giá trị
        // hay không, vì Cloudflare gán license_key cho mọi thiết bị đăng ký
        // (cả tài khoản Free, dùng cho referral). Phải hỏi thẳng API để biết
        // đúng trạng thái warp_plus thật.
        var status = await WarpAccountService.GetAccountStatusAsync(info);
        bool isPlusTier = status?.WarpPlus == true;
        UpdateTierDisplay(isPlusTier);
    }

    private void UpdateTierDisplay(bool isPlus)
    {
        if (isPlus)
        {
            AccountTierText.Text = "WARP+  ✅";
            AccountBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 185, 0));
            AccountIcon.Foreground  = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0));
            AccountIcon.Glyph       = "\uE8D4";
        }
        else
        {
            AccountTierText.Text = "WARP Free";
            AccountBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(26, 0, 120, 212));
            AccountIcon.Foreground  = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212));
            AccountIcon.Glyph       = "\uE8D4";
        }
    }

    private async void ApplyKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowStatus("⚠️  Vui lòng nhập License Key.", isError: true);
            return;
        }

        ApplyKeyBtn.IsEnabled = false;
        ApplyKeyBtn.Content   = "Đang kiểm tra...";
        StatusMsg.Visibility  = Visibility.Collapsed;

        var (success, message) = await WarpAccountService.UpdateLicenseAsync(key);

        ApplyKeyBtn.IsEnabled = true;
        ApplyKeyBtn.Content   = "Áp dụng Key";

        if (success)
        {
            ShowStatus($"✅  {message}", isError: false);
            UpdateTierDisplay(isPlus: true);
            // Lưu key vào account info
            var info = await WarpAccountService.GetOrCreateAccountAsync();
            LicenseKeyBox.Text = string.Empty;
        }
        else
        {
            ShowStatus($"❌  {message}", isError: true);
        }
    }

    private void ShowStatus(string msg, bool isError)
    {
        StatusMsg.Text       = msg;
        StatusMsg.Foreground = isError
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100));
        StatusMsg.Visibility = Visibility.Visible;
    }

    // ── License Key riêng cho MASQUE Beta (thiết bị đăng ký riêng — tốn thêm 1/5 slot) ──
    private async void ApplyMasqueKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        var key = MasqueLicenseKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowMasqueStatus("⚠️  Vui lòng nhập License Key.", isError: true);
            return;
        }

        ApplyMasqueKeyBtn.IsEnabled = false;
        ApplyMasqueKeyBtn.Content   = "Đang kiểm tra...";
        MasqueStatusMsg.Visibility  = Visibility.Collapsed;

        var (success, message) = await WarpAccountService.UpdateMasqueLicenseAsync(key);

        ApplyMasqueKeyBtn.IsEnabled = true;
        ApplyMasqueKeyBtn.Content   = "Áp dụng Key cho MASQUE";

        if (success)
        {
            ShowMasqueStatus($"✅  {message}", isError: false);
            MasqueLicenseKeyBox.Text = string.Empty;
        }
        else
        {
            ShowMasqueStatus($"❌  {message}", isError: true);
        }
    }

    private void ShowMasqueStatus(string msg, bool isError)
    {
        MasqueStatusMsg.Text       = msg;
        MasqueStatusMsg.Foreground = isError
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100));
        MasqueStatusMsg.Visibility = Visibility.Visible;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    private string? BrowseForFile(string title, string filter)
    {
        var ofn = new OpenFileName();
        ofn.lStructSize = Marshal.SizeOf(ofn);
        ofn.hwndOwner = (App.Current as App)!.MainWindowHandle;
        ofn.lpstrFilter = filter;
        ofn.lpstrFile = new string(new char[256]);
        ofn.nMaxFile = ofn.lpstrFile.Length;
        ofn.lpstrFileTitle = new string(new char[64]);
        ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
        ofn.lpstrTitle = title;
        ofn.Flags = 0x00080000 | 0x00001000 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR

        return GetOpenFileName(ref ofn) ? ofn.lpstrFile : null;
    }

    private async void ImportAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tomlPath = BrowseForFile(
                "Chọn file wgcf-account.toml",
                "wgcf-account.toml\0wgcf-account.toml\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(tomlPath)) return;

            // wgcf luôn tạo 2 file cùng thư mục — tự tìm file .conf đi kèm trước,
            // chỉ hỏi thêm nếu không tìm thấy.
            var sameDirConf = Path.Combine(Path.GetDirectoryName(tomlPath)!, "wgcf-profile.conf");
            string? confPath = File.Exists(sameDirConf) ? sameDirConf : null;
            confPath ??= BrowseForFile(
                "Chọn file wgcf-profile.conf",
                "wgcf-profile.conf\0wgcf-profile.conf\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(confPath)) return;

            ImportAccountBtn.IsEnabled = false;
            ImportAccountBtn.Content   = "Đang import...";

            var tomlContent = await File.ReadAllTextAsync(tomlPath);
            var confContent = await File.ReadAllTextAsync(confPath);
            var (success, message) = await WarpAccountService.ImportFromWgcfFilesAsync(tomlContent, confContent);

            ImportAccountBtn.IsEnabled = true;
            ImportAccountBtn.Content   = "📂  Chọn file wgcf-account.toml";

            ImportMsg.Text        = success ? $"✅  {message}" : $"❌  {message}";
            ImportMsg.Foreground  = success
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77));
            ImportMsg.Visibility  = Visibility.Visible;

            if (success)
            {
                await LoadAccountInfoAsync();
            }
        }
        catch (Exception ex)
        {
            ImportAccountBtn.IsEnabled = true;
            ImportAccountBtn.Content   = "📂  Chọn file wgcf-account.toml";
            ImportMsg.Text = $"❌  Lỗi: {ex.Message}";
            ImportMsg.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77));
            ImportMsg.Visibility = Visibility.Visible;
        }
    }

    private async void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        // Hiển thị dialog xác nhận trước khi xóa
        var dialog = new ContentDialog
        {
            XamlRoot            = Content.XamlRoot,
            Title               = "Xác nhận Reset tài khoản",
            Content             = "Bạn có chắc chắn muốn xóa tài khoản WARP hiện tại?\n\nKey WARP+ sẽ bị gỡ và một tài khoản WARP Free mới sẽ được tạo tự động khi bạn Boost lần sau.",
            PrimaryButtonText   = "🗑️  Xóa & Reset",
            CloseButtonText     = "Hủy",
            DefaultButton       = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        ResetBtn.IsEnabled = false;

        var (success, message) = await WarpAccountService.ResetToFreeAsync();

        ResetBtn.IsEnabled   = true;
        ResetMsg.Text        = success ? $"✅  {message}" : $"❌  {message}";
        ResetMsg.Foreground  = success
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77));
        ResetMsg.Visibility  = Visibility.Visible;

        if (success)
        {
            // Cập nhật lại UI về trạng thái Free
            UpdateTierDisplay(isPlus: false);
            AccountIdText.Text = "Chưa có tài khoản";
        }
    }
}
