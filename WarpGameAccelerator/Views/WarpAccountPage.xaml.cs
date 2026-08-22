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

    /// <summary>
    /// Trang này đang xem/tác động tài khoản MASQUE hay tài khoản WireGuard.
    /// Quyết định theo engine mode lúc mở trang (xem <see cref="LoadAccountInfoAsync"/>).
    /// </summary>
    private bool _isMasqueAccount;

    public WarpAccountPage()
    {
        InitializeComponent();
        _loc = App.Services.GetRequiredService<LocalizationService>();
        _ = LoadAccountInfoAsync();
    }

    /// <summary>
    /// Nạp thông tin tài khoản ĐANG THỰC SỰ ĐƯỢC DÙNG, chọn theo engine mode.
    ///
    /// SỬA 2026-08-22: trước đây trang này luôn đọc tài khoản WireGuard
    /// (warp_account.json). Từ v1.15.0 mặc định là Direct MASQUE, mà mode đó đăng ký
    /// THIẾT BỊ RIÊNG (warp_masque_account.json — id/token/license khác hẳn). Hệ quả
    /// của bản cũ: trang hiển thị tier của một tài khoản không được dùng, và nút
    /// "Áp dụng Key" nâng cấp sai tài khoản — người dùng nhập key WARP+ xong mà
    /// tunnel vẫn chạy Free, không có cách nào biết.
    /// </summary>
    private async Task LoadAccountInfoAsync()
    {
        var mode = ViewModels.SettingsViewModel.LoadEngineMode();
        _isMasqueAccount = mode == Models.EngineMode.DirectMasqueBeta;

        string id;
        (bool WarpPlus, string AccountType)? status;

        if (_isMasqueAccount)
        {
            var acc = await WarpAccountService.GetOrCreateMasqueAccountAsync();
            id     = acc.Id;
            status = await WarpAccountService.GetMasqueAccountStatusAsync(acc);
        }
        else
        {
            var acc = await WarpAccountService.GetOrCreateAccountAsync();
            id     = acc.Id;
            status = await WarpAccountService.GetAccountStatusAsync(acc);
        }

        AccountIdText.Text = string.IsNullOrEmpty(id)
            ? "Chưa có tài khoản"
            : $"ID: {id[..Math.Min(16, id.Length)]}...";

        // Hỏi thẳng API mới biết tier thật — KHÔNG suy từ field License, vì
        // Cloudflare gán license_key cho cả tài khoản Free (dùng cho referral).
        // API không trả lời được thì hiện "chưa xác định", không đoán là Free.
        UpdateTierDisplay(status?.WarpPlus, status?.AccountType);

        AccountScopeText.Text = mode switch
        {
            Models.EngineMode.DirectMasqueBeta => "Tài khoản của Direct MASQUE — đây là tunnel đang chạy.",
            Models.EngineMode.DirectWireGuard  => "Tài khoản của Direct WireGuard — đây là tunnel đang chạy.",
            _                                  => "Tài khoản của Direct WireGuard — KHÔNG phải tunnel đang chạy."
        };

        // WARP Client Proxy: tunnel do app WARP gốc dựng bằng tài khoản riêng của
        // nó, app này không chạm tới. Key nhập ở đây sẽ vào tài khoản WireGuard
        // đang không được dùng => phải nói rõ, không để người dùng nhập vô ích.
        bool proxyMode = mode == Models.EngineMode.WarpClientProxy;
        KeyScopeWarn.Text = proxyMode
            ? "⚠️  Mode WARP Client Proxy: tunnel do app WARP gốc quản lý, nên key nhập ở đây KHÔNG áp cho đường đang chạy. Hãy nhập key trong app 1.1.1.1, hoặc đổi sang Direct MASQUE / Direct WireGuard."
            : string.Empty;
        KeyScopeWarn.Visibility = proxyMode ? Visibility.Visible : Visibility.Collapsed;

        // File wgcf là định dạng của WireGuard — không dùng được cho MASQUE.
        bool importOffTarget = _isMasqueAccount || proxyMode;
        ImportScopeNote.Text = importOffTarget
            ? "⚠️  File wgcf chỉ áp cho tài khoản Direct WireGuard. Import ở đây KHÔNG đổi tài khoản của mode đang bật."
            : string.Empty;
        ImportScopeNote.Visibility = importOffTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Hiện tier. <paramref name="isPlus"/> = null nghĩa là KHÔNG hỏi được API
    /// (mất mạng, chưa có tài khoản, bị rate-limit) — khi đó phải nói "chưa xác
    /// định", không được hiện "WARP Free" vì đó là đoán (xem CLAUDE.md: không bịa
    /// chỉ số hiển thị).
    /// </summary>
    private void UpdateTierDisplay(bool? isPlus, string? accountType = null)
    {
        AccountIcon.Glyph = "\uE8D4";

        if (isPlus == true)
        {
            AccountTierText.Text    = "WARP+  ✅";
            AccountBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 185, 0));
            AccountIcon.Foreground  = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 185, 0));
        }
        else if (isPlus == false)
        {
            // account_type có 3 giá trị: free / limited / unlimited. Chỉ "unlimited"
            // là WARP+ thật, nhưng "limited" khác "free" nên ghi ra cho rõ.
            AccountTierText.Text = string.Equals(accountType, "limited", StringComparison.OrdinalIgnoreCase)
                ? "WARP Free (limited)"
                : "WARP Free";
            AccountBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(26, 0, 120, 212));
            AccountIcon.Foreground  = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212));
        }
        else
        {
            AccountTierText.Text    = "Chưa xác định được tier";
            AccountBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(26, 128, 128, 128));
            AccountIcon.Foreground  = new SolidColorBrush(ColorHelper.FromArgb(255, 150, 150, 150));
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

        // Áp vào tài khoản của mode đang bật — không phải luôn luôn WireGuard.
        var (success, message) = _isMasqueAccount
            ? await WarpAccountService.UpdateMasqueLicenseAsync(key)
            : await WarpAccountService.UpdateLicenseAsync(key);

        ApplyKeyBtn.IsEnabled = true;
        ApplyKeyBtn.Content   = "Áp dụng Key";

        if (success)
        {
            ShowStatus($"✅  {message}", isError: false);
            LicenseKeyBox.Text = string.Empty;
            // Đọc lại tier từ API thay vì mặc định coi là WARP+: server nhận key
            // không đồng nghĩa tier đã thành unlimited.
            await LoadAccountInfoAsync();
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
        var which = _isMasqueAccount ? "MASQUE" : "WireGuard";
        var dialog = new ContentDialog
        {
            XamlRoot            = Content.XamlRoot,
            Title               = "Xác nhận Reset tài khoản",
            Content             = $"Bạn có chắc chắn muốn xóa tài khoản WARP {which} hiện tại?\n\nKey WARP+ sẽ bị gỡ và một tài khoản WARP Free mới sẽ được tạo tự động khi bạn Boost lần sau.\n\nTài khoản của mode còn lại KHÔNG bị ảnh hưởng.",
            PrimaryButtonText   = "🗑️  Xóa & Reset",
            CloseButtonText     = "Hủy",
            DefaultButton       = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        ResetBtn.IsEnabled = false;

        var (success, message) = _isMasqueAccount
            ? await WarpAccountService.ResetMasqueAccountAsync()
            : await WarpAccountService.ResetToFreeAsync();

        ResetBtn.IsEnabled   = true;
        ResetMsg.Text        = success ? $"✅  {message}" : $"❌  {message}";
        ResetMsg.Foreground  = success
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 255, 77, 77));
        ResetMsg.Visibility  = Visibility.Visible;

        if (success)
        {
            // File tài khoản vừa bị xoá nên chưa có tier nào để nói. KHÔNG gọi
            // LoadAccountInfoAsync() ở đây: nó sẽ đăng ký ngay một tài khoản mới,
            // trái với thông báo "sẽ tự tạo lại khi Boost lần sau".
            UpdateTierDisplay(null);
            AccountIdText.Text = "Chưa có tài khoản";
        }
    }
}
