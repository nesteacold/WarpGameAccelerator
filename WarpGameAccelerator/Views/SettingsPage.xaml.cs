// ============================================================
// Views/SettingsPage.xaml.cs
// ============================================================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.ViewModels;

namespace WarpGameAccelerator.Views;

public sealed partial class SettingsPage : Page
{
    private readonly MihomoService _mihomoService;
    private readonly MasqueTunnelSweepService _tunnelSweep;
    public SettingsViewModel ViewModel { get; }
    public LocalizationService Loc { get; }

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        Loc       = App.Services.GetRequiredService<LocalizationService>();
        _mihomoService = App.Services.GetRequiredService<MihomoService>();
        _tunnelSweep   = App.Services.GetRequiredService<MasqueTunnelSweepService>();

        Loc.PropertyChanged += (_, __) => UpdateLanguageButtons();
        UpdateLanguageButtons();
        _tunnelSweep.SweepUpdated += (_, statuses) => DispatcherQueue.TryEnqueue(() => RefreshMasqueQuickBadge(statuses));
        RefreshMasqueQuickBadge(_tunnelSweep.LatestSnapshot());
    }

    /// <summary>Mở panel phụ "Tìm & giữ colo" — nội dung thật sống trong
    /// MasqueColoPanel/MainWindow, xem MainWindow.ShowMasqueColoPanel.</summary>
    private void OpenMasquePanel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => App.Services.GetRequiredService<MainWindow>().ShowMasqueColoPanel();

    /// <summary>Huy hiệu colo nhỏ trên dòng mở panel — chỉ hiện khi tính năng giữ nhiều
    /// tunnel dự phòng đang bật VÀ có tunnel nào đó đang thật sự mang traffic game, đọc
    /// từ số đo gần nhất (KHÔNG suy đoán/bịa — ẩn hẳn nếu chưa có số đo nào).</summary>
    private void RefreshMasqueQuickBadge(IReadOnlyList<CandidateColoStatus> statuses)
    {
        var current = statuses.FirstOrDefault(s => s.IsCurrentSelection && s.Colo != null);
        if (current == null || _mihomoService.ActiveMasqueControl == null)
        {
            MasqueQuickColoBadge.Visibility = Visibility.Collapsed;
            return;
        }
        MasqueQuickColoText.Text = $"colo {current.Colo}";
        MasqueQuickColoBadge.Visibility = Visibility.Visible;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshMasqueQuickBadge(_tunnelSweep.LatestSnapshot());
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
