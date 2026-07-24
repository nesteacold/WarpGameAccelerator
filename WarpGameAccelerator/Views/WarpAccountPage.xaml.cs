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
    private readonly WarpAccountService _svc;
    private readonly LocalizationService _loc;

    public WarpAccountPage()
    {
        InitializeComponent();
        _svc = App.Services.GetRequiredService<WarpAccountService>();
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
}
