// ============================================================
// Views/SettingsPage.xaml.cs
// ============================================================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.ViewModels;

namespace WarpGameAccelerator.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    public LocalizationService Loc { get; }

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        Loc       = App.Services.GetRequiredService<LocalizationService>();

        Loc.PropertyChanged += (_, __) => UpdateLanguageButtons();
        UpdateLanguageButtons();
    }

    /// <summary>Cập nhật visual của 2 nút VIE/ENG theo ngôn ngữ hiện tại</summary>
    private void UpdateLanguageButtons()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            bool isVi = ViewModel.IsVietnamese;
            BtnVi.Style = isVi
                ? (Style)Application.Current.Resources["AccentButtonStyle"]
                : (Style)Application.Current.Resources["DefaultButtonStyle"];
            BtnEn.Style = isVi
                ? (Style)Application.Current.Resources["DefaultButtonStyle"]
                : (Style)Application.Current.Resources["AccentButtonStyle"];
        });
    }
}
