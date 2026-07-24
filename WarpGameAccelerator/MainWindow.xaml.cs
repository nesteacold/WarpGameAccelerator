using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WarpGameAccelerator.Helpers;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.Views;
using WarpGameAccelerator.ViewModels;
using Windows.Graphics;

namespace WarpGameAccelerator;

public sealed partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashboardVm;
    private readonly LocalizationService _loc;
    private TrayIconHelper? _trayIcon;

    public MainWindow(DashboardViewModel dashboardVm)
    {
        InitializeComponent();
        _dashboardVm = dashboardVm;
        _loc         = App.Services.GetRequiredService<LocalizationService>();

        // Đọc version động từ Assembly metadata
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.4.0";

        ConfigureWindow();
        ConfigureSystemBackdrop();
        ConfigureTrayIcon();
        SubscribeDashboardEvents();

        // Subscribe language change để update nav items
        _loc.PropertyChanged += (_, __) => UpdateNavItemLabels();

        // Navigate to Dashboard by default
        ContentFrame.Navigate(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];

        UpdateNavItemLabels();
    }

    // ── Window configuration ─────────────────────────────────

    private void ConfigureWindow()
    {
        // Fixed size 420 × 640
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(420, 640));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));

        // Disable resize
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable   = false;
            presenter.IsMaximizable = false;
        }

        // Custom titlebar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Customize caption button colors to match dark theme
        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor         = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor    = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // Custom close behavior with prompt
        appWindow.Closing += (s, args) =>
        {
            if (!_isActuallyExiting)
            {
                args.Cancel = true;
                _ = PromptExitAsync();
            }
        };
    }

    private void UpdateNavItemLabels()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // MenuItems: [0]=Dashboard, [1]=Chọn Game
            if (NavView.MenuItems[1] is NavigationViewItem selectGame)
                selectGame.Content = _loc.NavSelectGame;

        // FooterMenuItems: [0]=MultiClient, [1]=WarpAccount, [2]=Settings, [3]=Exit
            if (NavView.FooterMenuItems[2] is NavigationViewItem settings)
                settings.Content = _loc.NavSettings;
            if (NavView.FooterMenuItems[3] is NavigationViewItem exit)
                exit.Content = _loc.NavExit;
        });
    }


    private bool _isActuallyExiting = false;

    private async Task PromptExitAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot            = Content.XamlRoot,
            Style               = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Title               = _loc.ExitTitle,
            Content             = _loc.ExitMessage,
            PrimaryButtonText   = _loc.ExitBtnExit,
            SecondaryButtonText = _loc.ExitBtnMinimize,
            CloseButtonText     = _loc.ExitBtnCancel,
            DefaultButton       = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            _isActuallyExiting = true;
            ExitApp();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            AppWindow.Hide();
            _trayIcon?.ShowBalloon(
                "WARP Game Accelerator",
                _loc.TrayMinimizedMsg);
        }
    }

    private void ConfigureSystemBackdrop()
    {
        SystemBackdrop = new MicaBackdrop();
    }

    // ── System Tray ──────────────────────────────────────────

    private void ConfigureTrayIcon()
    {
        _trayIcon = new TrayIconHelper(
            onShowWindow:  ShowMainWindow,
            onDisconnect:  async () => await _dashboardVm.ToggleBoostCommand.ExecuteAsync(null),
            onExit:        ExitApp
        );
    }

    private void ShowMainWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            AppWindow.MoveInZOrderAtTop();
        });
    }

    private async void ExitApp()
    {
        _trayIcon?.Dispose();

        try
        {
            // Thực hiện dọn dẹp với timeout 3 giây để tránh bị treo app
            var cleanupTask = Task.Run(async () =>
            {
                var warpSvc = App.Services.GetRequiredService<IWarpService>();
                var mihomoSvc = App.Services.GetRequiredService<MihomoService>();

                // Ngắt Cloudflare WARP trước
                await warpSvc.DisconnectAsync();
                
                // Dừng Mihomo và Dashboard
                mihomoSvc.StopProxy();
                await _dashboardVm.HandleAppExitAsync();
            });

            await Task.WhenAny(cleanupTask, Task.Delay(3000));
        }
        catch { }

        Application.Current.Exit();
        Environment.Exit(0); // Force kill to prevent background thread hangs
    }

    // ── Dashboard events → titlebar status ──────────────────

    private void SubscribeDashboardEvents()
    {
        _dashboardVm.BoostStarted += () => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatusBadge(connected: true);
            _trayIcon?.SetConnected(true);
        });

        _dashboardVm.BoostStopped += () => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatusBadge(connected: false);
            _trayIcon?.SetConnected(false);
        });
    }

    private void UpdateStatusBadge(bool connected)
    {
        if (connected)
        {
            StatusBadgeText.Text       = "● ACTIVE";
            StatusBadgeText.Foreground = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 0, 212, 255));
            StatusBadge.Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(30, 0, 212, 255));
        }
        else
        {
            StatusBadgeText.Text       = "● IDLE";
            StatusBadgeText.Foreground = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 136, 136, 136));
            StatusBadge.Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(26, 0, 212, 255));
        }
    }

    // Bỏ hàm CheckForUpdatesAsync() ở MainWindow vì User tự check trong Settings

    // ── Navigation ───────────────────────────────────────────

    private void NavView_ItemInvoked(NavigationView sender,
                                     NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;

        var tag = item.Tag?.ToString();
        if (tag == "exit")
        {
            _ = PromptExitAsync();
            return;
        }

        Type? pageType = tag switch
        {
            "dashboard"   => typeof(DashboardPage),
            "process"     => typeof(ProcessPickerPage),
            "multiclient" => typeof(MultiClientPage),
            "warpaccount" => typeof(WarpAccountPage),
            "settings"    => typeof(SettingsPage),
            _             => null
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    public void NavigateToDashboard()
    {
        NavView.SelectedItem = NavView.MenuItems[0];
        if (ContentFrame.CurrentSourcePageType != typeof(DashboardPage))
            ContentFrame.Navigate(typeof(DashboardPage));
    }
}
