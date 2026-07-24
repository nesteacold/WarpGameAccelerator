// ============================================================
// Views/WarpAccountPage.xaml.cs — Code-behind cho màn hình WARP+ Account
// ============================================================
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

        // Hiển thị trạng thái tài khoản
        bool isPlusTier = !string.IsNullOrEmpty(info.License);
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
