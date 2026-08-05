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
using System.Runtime.InteropServices;

namespace WarpGameAccelerator;

public sealed partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashboardVm;
    private readonly LocalizationService _loc;
    private TrayIconHelper? _trayIcon;
    // Developer Panel — có trong MỌI build kể cả Release public (theo yêu
    // cầu người dùng). Không mật khẩu, chỉ ẩn qua hotkey — ai biết tổ hợp
    // phím Ctrl+Shift+Alt+D đều mở được, không giới hạn máy nào.
    private GlobalHotkeyHelper? _devPanelHotkey;

    public MainWindow(DashboardViewModel dashboardVm)
    {
        InitializeComponent();
        _dashboardVm = dashboardVm;
        _loc         = App.Services.GetRequiredService<LocalizationService>();

        // Đọc version động từ Assembly metadata — KHÔNG hard-code, tránh
        // trường hợp bump version trong .csproj mà quên sửa UI.
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText.Text = ver is not null
            ? $"Version {ver.Major}.{ver.Minor}.{ver.Build}"
            : "Version —";

        ConfigureWindow();
        ConfigureSystemBackdrop();
        ConfigureTrayIcon();
        ConfigureDevPanelHotkey();
        WireDevPanelEvents();
        SubscribeDashboardEvents();

        // Subscribe language change để update nav items
        _loc.PropertyChanged += (_, __) => UpdateNavItemLabels();

        // Navigate to Dashboard by default
        ContentFrame.Navigate(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];

        UpdateNavItemLabels();

        // Kiểm tra & đề xuất gửi báo cáo lỗi nếu lần trước bị sập
        CheckPendingCrashReportAsync();
    }

    private async void CheckPendingCrashReportAsync()
    {
        try
        {
            if (CrashReportService.HasPendingCrashReport())
            {
                var info = CrashReportService.GetPendingCrashReport();
                if (info != null)
                {
                    await System.Threading.Tasks.Task.Delay(1200); // Chờ UI nạp hoàn chỉnh
                    var dialog = new Dialogs.CrashReportDialog(info)
                    {
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
        }
        catch
        {
            // Bảo vệ 100%: Fail-safe im lặng
            CrashReportService.ClearPendingCrashReport();
        }
    }

    // ── Window configuration ─────────────────────────────────

    private void ConfigureWindow()
    {
        // Fixed size 520 × 680
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(520, 680));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));

        // Căn giữa màn hình
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var centeredX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - 520) / 2;
            var centeredY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - 680) / 2;
            appWindow.Move(new PointInt32(centeredX, centeredY));
        }

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

        // FooterMenuItems: [0]=MultiClient, [1]=AowBooster, [2]=WarpAccount, [3]=Settings, [4]=Exit
            if (NavView.FooterMenuItems[3] is NavigationViewItem settings)
                settings.Content = _loc.NavSettings;
            if (NavView.FooterMenuItems[4] is NavigationViewItem exit)
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

    // ── Developer Panel hotkey (ẩn — Ctrl+Shift+Alt+D, có trong MỌI build
    // kể cả Release public) ──
    // Dùng GlobalHotkeyHelper (hidden message-only window, không subclass
    // WndProc của MainWindow — đã spike xác nhận hoạt động ổn định). Callback
    // chạy trên thread message-pump riêng — phải nhảy về UI thread qua
    // DispatcherQueue trước khi làm gì với XAML/dialog.
    //
    // Không có mật khẩu — chỉ ẩn qua hotkey, ai biết tổ hợp phím đều mở được
    // (kể cả người dùng bản public tải từ GitHub Releases). Bấm hotkey =
    // toggle mở/đóng panel.
    private bool _devPanelToggling = false;

    private void ConfigureDevPanelHotkey()
    {
        const uint VK_D = 0x44;
        _devPanelHotkey = new GlobalHotkeyHelper(
            GlobalHotkeyHelper.MOD_CONTROL | GlobalHotkeyHelper.MOD_SHIFT | GlobalHotkeyHelper.MOD_ALT,
            VK_D,
            onHotkeyPressed: () => DispatcherQueue.TryEnqueue(ToggleDeveloperPanel));

        if (!_devPanelHotkey.IsRegistered)
        {
            DiagnosticLogService.Trace("[DevPanel] Không đăng ký được hotkey — có thể bị app khác chiếm tổ hợp phím.");
        }
    }

    /// <summary>
    /// Bọc try/catch quanh TOÀN BỘ luồng mở/đóng panel — đây là callback chạy
    /// trực tiếp từ DispatcherQueue.TryEnqueue (không có async Task nào bên
    /// ngoài bắt exception hộ), một exception thoát ra khỏi đây sẽ crash cả
    /// process (đúng loại lỗi đã ghi ở CLAUDE.md mục Process lifecycle).
    /// </summary>
    private void ToggleDeveloperPanel()
    {
        if (_devPanelToggling) return;
        _devPanelToggling = true;

        try
        {
            if (DevPanelRoot.Visibility == Visibility.Visible)
                CloseDeveloperPanel();
            else
                ShowDeveloperPanel();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[DevPanel] Lỗi khi mở/đóng panel: {ex}");
        }
        finally
        {
            _devPanelToggling = false;
        }
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
        _devPanelHotkey?.Dispose();

        try
        {
            // Thực hiện dọn dẹp với timeout 3 giây để tránh bị treo app
            var cleanupTask = Task.Run(async () =>
            {
                var warpSvc = App.Services.GetRequiredService<IWarpService>();
                var mihomoSvc = App.Services.GetRequiredService<MihomoService>();

                // Dừng Mihomo TRƯỚC (nhanh, không phụ thuộc network) — nếu bước
                // warpSvc.DisconnectAsync() bên dưới bị treo và hết timeout 3s,
                // Environment.Exit(0) vẫn đảm bảo mihomo.exe đã được kill, không
                // để lại tiến trình mồ côi giữ nguyên tunnel WireGuard sau khi
                // app đã tắt.
                mihomoSvc.StopProxy();
                await _dashboardVm.HandleAppExitAsync();

                // Ngắt Cloudflare WARP (có thể chậm nếu warp-cli phản hồi trễ)
                await warpSvc.DisconnectAsync();
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
            "aowbooster"  => typeof(AowBoosterPage),
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

    // ══════════════════════════════════════════════════════════
    // Developer Panel — gộp vào chính MainWindow, có trong MỌI build kể cả
    // Release public. Mở/đóng bằng cách resize AppWindow rộng thêm để lộ cột
    // DevPanelColumn (xem MainWindow.xaml).
    // ══════════════════════════════════════════════════════════
    private const int MainWindowWidth = 520;
    private const int MainWindowHeight = 680;
    private const int DevPanelWidth = 380;

    /// <summary>
    /// Wire toàn bộ event handler của Dev Panel bằng code (KHÔNG dùng
    /// Click="..."/Toggled="..." trong XAML) — không liên quan gì tới build
    /// config nữa (Dev Panel giờ có ở mọi build), chỉ đơn giản là cách wiring
    /// nhất quán, tránh phải sửa lại XAML nếu sau này cần điều kiện hoá gì.
    /// </summary>
    private void WireDevPanelEvents()
    {
        CloseDevPanelBtn.Click          += CloseDevPanelBtn_Click;
        ImportConfigBtn.Click           += ImportConfigBtn_Click;
        PickProcessBtn.Click            += PickProcessBtn_Click;
        PersonalBoostToggle.Toggled     += PersonalBoostToggle_Toggled;
        ExcludedTunnelCombo.SelectionChanged += ExcludedTunnelCombo_SelectionChanged;
        ProfileListView.SelectionChanged += ProfileListView_SelectionChanged;
        ProfileListView.ContainerContentChanging += ProfileListView_ContainerContentChanging;
        MasqueEngineToggle.Toggled      += MasqueEngineToggle_Toggled;
        ApplyMasqueKeyBtn.Click         += ApplyMasqueKeyBtn_Click;
    }

    private void ProfileListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not Grid grid) return;
        if (grid.FindName("ProfileDeleteBtn") is not Button deleteBtn) return;

        deleteBtn.Click -= DeleteProfileBtn_Click;
        deleteBtn.Click += DeleteProfileBtn_Click;
    }

    public void ShowDeveloperPanel()
    {
        DevPanelColumn.Width = new GridLength(DevPanelWidth);
        DevPanelRoot.Visibility = Visibility.Visible;
        ResizeAndRecenter(MainWindowWidth + DevPanelWidth, MainWindowHeight);
        LoadPersonalVpnState();
        _ = LoadExcludedTunnelOptionsAsync();

        // Đặt IsOn KHÔNG qua event Toggled (đang subscribe) để tránh set lại
        // EngineMode/ghi file ngay khi chỉ đang mở panel để xem trạng thái.
        MasqueEngineToggle.Toggled -= MasqueEngineToggle_Toggled;
        MasqueEngineToggle.IsOn = App.Services.GetRequiredService<SettingsViewModel>().IsDirectMasqueBeta;
        MasqueEngineToggle.Toggled += MasqueEngineToggle_Toggled;
    }

    private void CloseDeveloperPanel()
    {
        DevPanelRoot.Visibility = Visibility.Collapsed;
        DevPanelColumn.Width = new GridLength(0);
        ResizeAndRecenter(MainWindowWidth, MainWindowHeight);
    }

    private void CloseDevPanelBtn_Click(object sender, RoutedEventArgs e) => CloseDeveloperPanel();

    private void ResizeAndRecenter(int width, int height)
    {
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(width, height));

        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var centeredX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - width) / 2;
            var centeredY = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - height) / 2;
            appWindow.Move(new PointInt32(centeredX, centeredY));
        }
    }

    // ── Kênh VPN cá nhân (multi-profile) ─────────────────────

    private void LoadPersonalVpnState()
    {
        var store = PersonalVpnService.GetStore();
        var active = store.Profiles.FirstOrDefault(p => p.Id == store.ActiveProfileId);

        ProfileListView.ItemsSource = store.Profiles
            .Select(p => new PersonalVpnProfileItem { Id = p.Id, Name = p.Name, Endpoint = p.Endpoint })
            .ToList();
        // Set SelectedItem KHÔNG qua event SelectionChanged (đang subscribe) —
        // nếu không, handler gọi lại LoadPersonalVpnState() → set SelectedItem
        // lại → bắn SelectionChanged lại → đệ quy vô hạn → StackOverflowException
        // (loại duy nhất .NET không cho catch, crash im lặng không kịp ghi log).
        ProfileListView.SelectionChanged -= ProfileListView_SelectionChanged;
        if (active != null)
            ProfileListView.SelectedItem = (ProfileListView.ItemsSource as List<PersonalVpnProfileItem>)
                ?.FirstOrDefault(i => i.Id == active.Id);
        ProfileListView.SelectionChanged += ProfileListView_SelectionChanged;

        UpdatePersonalVpnStatusBadge(store.IsActive);
        PersonalBoostToggle.IsOn = store.IsActive;
        PickProcessBtn.Content = $"Chọn process ({active?.ProcessNames.Count ?? 0})";

        if (active != null && active.ProcessNames.Count > 0)
        {
            SelectedProcessesText.Text = string.Join(", ", active.ProcessNames);
            SelectedProcessesText.Visibility = Visibility.Visible;
        }
        else
        {
            SelectedProcessesText.Visibility = Visibility.Collapsed;
        }

        if (active != null && !string.IsNullOrEmpty(active.PeerPublicKey))
        {
            TunnelInfoPanel.Visibility = Visibility.Visible;
            InfoAddressText.Text     = string.IsNullOrEmpty(active.AddressV6)
                ? active.AddressV4
                : $"{active.AddressV4}, {active.AddressV6}";
            InfoDnsText.Text         = string.IsNullOrEmpty(active.Dns) ? "—" : active.Dns;
            InfoPeerKeyText.Text     = Truncate(active.PeerPublicKey, 24);
            InfoEndpointText.Text    = active.Endpoint;
            InfoAllowedIpsText.Text  = active.AllowedIPs;
        }
        else
        {
            TunnelInfoPanel.Visibility = Visibility.Collapsed;
        }

        // Ghi chú nhỏ thay cho popup — không làm gián đoạn người dùng.
        if (store.Profiles.Count == 0)
        {
            PersonalVpnHintText.Text = "Chưa có profile — bấm \"Import file wg-quick .conf\" để bắt đầu.";
            PersonalVpnHintText.Visibility = Visibility.Visible;
        }
        else if (active != null && active.ProcessNames.Count == 0)
        {
            PersonalVpnHintText.Text = "Chưa chọn process — bấm \"Chọn process\" trước khi Boost kênh này.";
            PersonalVpnHintText.Visibility = Visibility.Visible;
        }
        else
        {
            PersonalVpnHintText.Visibility = Visibility.Collapsed;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private void UpdatePersonalVpnStatusBadge(bool active)
    {
        if (active)
        {
            PersonalVpnStatusText.Text = "Active";
            PersonalVpnStatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 212, 255));
            PersonalVpnStatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(26, 0, 212, 255));
        }
        else
        {
            PersonalVpnStatusText.Text = "Inactive";
            PersonalVpnStatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 170, 170, 170));
            PersonalVpnStatusBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(26, 136, 136, 136));
        }
    }

    private async void ImportConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // P/Invoke GetOpenFileName (comdlg32) — KHÔNG dùng Windows.Storage.Pickers.
            // FileOpenPicker (WinRT): app này build unpackaged (WindowsPackageType=None,
            // AppxPackage=false trong .csproj) — FileOpenPicker cần package identity,
            // trên Win32 app unpackaged nó throw COMException 0x80004005 (E_FAIL) khi
            // gọi PickSingleFileAsync() (đã xác nhận qua trace.log). Đây cũng là lý do
            // WarpAccountPage.xaml.cs từ trước dùng đúng P/Invoke này, không dùng picker.
            var confPath = BrowseForFile(
                "Chọn file WireGuard .conf",
                "WireGuard Config (*.conf)\0*.conf\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(confPath)) return;

            ImportConfigBtn.IsEnabled = false;

            var content = await File.ReadAllTextAsync(confPath);
            // Tên profile tự lấy theo tên file .conf (giống WireGuard client chính
            // thức dùng tên tunnel = tên file) — không hỏi lại người dùng.
            var displayName = Path.GetFileNameWithoutExtension(confPath);
            var (success, message) = PersonalVpnService.ImportConfig(content, displayName);

            ImportConfigBtn.IsEnabled = true;
            ImportMsg.Text = success ? $"✅  {message}" : $"❌  {message}";
            ImportMsg.Visibility = Visibility.Visible;

            if (success)
            {
                LoadPersonalVpnState();
                if (PersonalVpnService.IsChannelActive())
                    await App.Services.GetRequiredService<MihomoService>().ApplyPersonalProfileChangeAsync();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[DevPanel] Import .conf lỗi: {ex}");
            ImportConfigBtn.IsEnabled = true;
            ImportMsg.Text = $"❌  Lỗi ({ex.GetType().Name}): {ex.Message}";
            ImportMsg.Visibility = Visibility.Visible;
        }
    }

    private async void ProfileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileListView.SelectedItem is not PersonalVpnProfileItem item) return;

        PersonalVpnService.SetActiveProfile(item.Id);
        LoadPersonalVpnState();

        if (PersonalVpnService.IsChannelActive())
            await App.Services.GetRequiredService<MihomoService>().ApplyPersonalProfileChangeAsync();
    }

    private async void DeleteProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string profileId) return;

        PersonalVpnService.DeleteProfile(profileId);
        LoadPersonalVpnState();

        if (PersonalVpnService.IsChannelActive())
            await App.Services.GetRequiredService<MihomoService>().ApplyPersonalProfileChangeAsync();
    }

    private async void PickProcessBtn_Click(object sender, RoutedEventArgs e)
    {
        // Chưa có profile — ghi chú nhỏ (PersonalVpnHintText, set ở LoadPersonalVpnState)
        // đã nói rõ điều này, không cần popup chặn luồng.
        var active = PersonalVpnService.GetActiveProfile();
        if (active == null) return;

        var processService = App.Services.GetRequiredService<ProcessService>();
        var dialog = new MultiProcessPickerDialog(processService, active.ProcessNames)
        {
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var selected = dialog.GetSelectedProcessNames();
        PersonalVpnService.SaveSelectedProcesses(active.Id, selected);
        PickProcessBtn.Content = $"Chọn process ({selected.Count})";
        SelectedProcessesText.Text = string.Join(", ", selected);
        SelectedProcessesText.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (PersonalVpnService.IsChannelActive())
            await App.Services.GetRequiredService<MihomoService>().ApplyPersonalProfileChangeAsync();
    }

    private async void PersonalBoostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (PersonalBoostToggle.IsOn)
        {
            var active = PersonalVpnService.GetActiveProfile();
            bool valid = active != null
                && !string.IsNullOrWhiteSpace(active.PrivateKey)
                && !string.IsNullOrWhiteSpace(active.PeerPublicKey)
                && !string.IsNullOrWhiteSpace(active.Endpoint)
                && active.ProcessNames.Count > 0;

            if (!valid)
            {
                // Chưa sẵn sàng (thiếu profile/process) — revert toggle âm thầm,
                // PersonalVpnHintText đã giải thích lý do, không cần popup.
                PersonalBoostToggle.Toggled -= PersonalBoostToggle_Toggled;
                PersonalBoostToggle.IsOn = false;
                PersonalBoostToggle.Toggled += PersonalBoostToggle_Toggled;
                return;
            }
        }

        await App.Services.GetRequiredService<MihomoService>().SetPersonalChannelActiveAsync(PersonalBoostToggle.IsOn);
        UpdatePersonalVpnStatusBadge(PersonalBoostToggle.IsOn);
    }

    private const string NoExclusionOption = "(Không loại trừ)";

    /// <summary>
    /// Tự phát hiện danh sách tunnel "WireGuardTunnel$*" đã cài trên máy thay
    /// vì bắt gõ tay (dễ sai tên, gây bug im lặng như đã gặp). Giữ lại giá trị
    /// cũ trong list dù tunnel đó hiện không phát hiện được (máy khác/gỡ rồi),
    /// để không tự xoá mất lựa chọn người dùng đã lưu.
    /// </summary>
    private async Task LoadExcludedTunnelOptionsAsync()
    {
        try
        {
            var names = await WireGuardConflictGuard.GetAvailableTunnelNamesAsync();
            var current = PersonalVpnService.GetExcludedTunnelServiceName();

            var options = new List<string> { NoExclusionOption };
            options.AddRange(names);
            if (!string.IsNullOrEmpty(current) && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
                options.Add(current);

            ExcludedTunnelCombo.SelectionChanged -= ExcludedTunnelCombo_SelectionChanged;
            ExcludedTunnelCombo.ItemsSource = options;
            ExcludedTunnelCombo.SelectedItem = string.IsNullOrEmpty(current)
                ? NoExclusionOption
                : options.FirstOrDefault(o => o.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? NoExclusionOption;
            ExcludedTunnelCombo.SelectionChanged += ExcludedTunnelCombo_SelectionChanged;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[DevPanel] Lỗi load danh sách tunnel: {ex.Message}");
        }
    }

    private void ExcludedTunnelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ExcludedTunnelCombo.SelectedItem as string;
        var value = string.IsNullOrEmpty(selected) || selected == NoExclusionOption ? string.Empty : selected;

        // Cấp toàn máy, KHÔNG phụ thuộc có profile Active hay không — máy đóng
        // vai WireGuard server không cần import client profile nào cả.
        PersonalVpnService.SetExcludedTunnelServiceName(value);
    }

    // ── Direct Mode MASQUE (Beta) — dời từ SettingsPage/WarpAccountPage ──

    private void MasqueEngineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        // Toggle nhị phân (khác RadioButton) — tắt phải trả về mặc định
        // DirectWireGuard tường minh, vì setter IsDirectMasqueBeta chỉ phản
        // ứng với value=true (giống hành vi radio, không tự "bỏ chọn").
        settingsVm.EngineMode = MasqueEngineToggle.IsOn
            ? Models.EngineMode.DirectMasqueBeta
            : Models.EngineMode.DirectWireGuard;
    }

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

    // ── File picker (P/Invoke comdlg32 — giống pattern WarpAccountPage.xaml.cs,
    // bắt buộc vì app unpackaged, FileOpenPicker WinRT không dùng được) ──

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
        ofn.hwndOwner = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ofn.lpstrFilter = filter;
        ofn.lpstrFile = new string(new char[256]);
        ofn.nMaxFile = ofn.lpstrFile.Length;
        ofn.lpstrFileTitle = new string(new char[64]);
        ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
        ofn.lpstrTitle = title;
        ofn.Flags = 0x00080000 | 0x00001000 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR

        return GetOpenFileName(ref ofn) ? ofn.lpstrFile : null;
    }
}
