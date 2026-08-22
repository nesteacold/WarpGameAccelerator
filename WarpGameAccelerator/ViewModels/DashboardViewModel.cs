// ============================================================
// ViewModels/DashboardViewModel.cs — Logic màn hình chính
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IWarpService _warpService;
    private readonly PingMonitorService _pingMonitor;
    private readonly MihomoService _mihomoService;
    private readonly LocalizationService _loc;
    private readonly GameProfileService _profileService;
    private readonly DispatcherQueue _dispatcher;
    private readonly NetworkOptimizerService _networkOptimizer;

    // ── Observable Properties ────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanBoost))]
    [NotifyPropertyChangedFor(nameof(BoostButtonLabel))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private AppState _currentState = AppState.Idle;

    [ObservableProperty] private string _selectedProcessName = string.Empty;
    private GameProfile? _selectedProfile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    private long _currentPingMs = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaselineDisplay))]
    private long _baselinePingMs = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LossDisplay))]
    private double _packetLossPercent = 0.0;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private List<long> _pingHistory = new();

    // Route/tier hiển thị ở Dashboard — trước đây bị hardcode text "WARP+" trong
    // XAML, không phản ánh trạng thái tài khoản thật (khác biệt với WarpAccountPage
    // đã bind đúng). Cập nhật thật sau mỗi lần Boost thành công.
    [ObservableProperty] private string _routeTierText = "—";

    // Trang thai THAT cua duong toi server game, suy ra tu log dial cua mihomo
    // (MihomoService.LastGameDialFailureUtc). Day la thay the cho o PING cu —
    // o do lay so tu PingNodeAsync (RTT edge + hang so theo nhan node) nen no
    // hien binh thuong ngay ca khi moi ket noi toi server game dang timeout.
    [ObservableProperty] private string _gameLinkText = "—";
    [ObservableProperty] private Microsoft.UI.Xaml.Media.SolidColorBrush _gameLinkBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 200, 200, 200));
    [ObservableProperty] private Microsoft.UI.Xaml.Media.SolidColorBrush _routeTierBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 200, 200, 200));

    /// <summary>Expose LocalizationService cho x:Bind trong XAML</summary>
    public LocalizationService Loc => _loc;

    public bool IsIdle       => CurrentState == AppState.Idle;
    public bool IsConnected  => CurrentState == AppState.Connected;
    public bool IsConnecting => CurrentState is AppState.Connecting or AppState.Disconnecting;
    public bool HasError     => CurrentState == AppState.Error;

    // Expose as property for x:Bind (renamed to avoid conflict with RelayCommand CanExecute method)
    public bool CanBoost =>
        CurrentState is AppState.Idle or AppState.Connected or AppState.Error;

    // ── Display string properties (XAML-friendly, avoid complex converter chains) ──
    // -1 = KHONG do duoc (khong phai 0). Xem GameLinkText de biet duong toi
    // server game co dang loi hay khong — do moi la thong tin quyet dinh.
    // O the rong 1/3 man hinh, chuoi dai o FontSize 22 bi cat ("khong do d..."),
    // nen trang thai khong do duoc dung dau gach ngang; ly do nam o caption + ToolTip.
    public string PingDisplay      => CurrentPingMs > 0   ? $"{CurrentPingMs} ms" : "—";
    public string BaselineDisplay  => BaselinePingMs > 0  ? $"{BaselinePingMs} ms" : "-- ms";
    public string LossDisplay      => PacketLossPercent < 0 ? "—" : $"{PacketLossPercent:F1} %";

    /// <summary>Tên hiển thị thân thiện: dùng tên Game Profile nếu có, không thì dùng tên exe thô</summary>
    public string GameDisplayName  => _selectedProfile?.Name ?? (string.IsNullOrEmpty(SelectedProcessName) ? _loc.DashNoGameSelected : SelectedProcessName);

    public string BoostButtonLabel => CurrentState switch
    {
        AppState.Idle          => _loc.DashBoostNow,
        AppState.Connecting    => _loc.DashConnecting,
        AppState.Connected     => _loc.DashStop,
        AppState.Disconnecting => _loc.DashDisconnecting,
        AppState.Error         => _loc.DashRetry,
        _                      => _loc.DashBoostNow
    };

    public string StatusText => CurrentState switch
    {
        AppState.Idle       => _loc.DashStatusReady,
        AppState.Connecting => _loc.DashStatusConnecting,
        AppState.Connected  => _loc.DashStatusActive,
        AppState.Error      => $"\u274C {(_loc.IsVietnamese ? "L\u1ed7i" : "Error")}: {ErrorMessage}",
        _                   => string.Empty
    };

    // ── Events ───────────────────────────────────────────────

    public event Action? BoostStarted;
    public event Action? BoostStopped;

    // ── Constructor ──────────────────────────────────────────

    public DashboardViewModel(IWarpService warpService,
                              PingMonitorService pingMonitor,
                              MihomoService mihomoService,
                              LocalizationService loc,
                              GameProfileService profileService,
                              DispatcherQueue dispatcher)
    {
        _warpService   = warpService;
        _pingMonitor   = pingMonitor;
        _mihomoService = mihomoService;
        _loc           = loc;
        _profileService = profileService;
        _dispatcher    = dispatcher;
        _networkOptimizer = new NetworkOptimizerService();

        _pingMonitor.PingUpdated += OnPingUpdated;

        // Khi ngôn ngữ thay đổi, cập nhật toàn bộ display strings
        _loc.PropertyChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(BoostButtonLabel));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(GameDisplayName));
            OnPropertyChanged(nameof(Loc));
        };

        // Khôi phục & tự Boost lại phiên trước nếu app bị crash lúc đang Connected
        _ = TryRestoreLastSessionAsync();
    }

    private async Task TryRestoreLastSessionAsync()
    {
        var state = BoostStateService.LoadState();
        if (state == null || !state.WasConnected || string.IsNullOrWhiteSpace(state.ProcessName))
            return;

        if (!string.IsNullOrEmpty(state.ProfileName))
        {
            var profile = _profileService.All.FirstOrDefault(p => p.Name == state.ProfileName);
            if (profile != null) SetSelectedProfile(profile);
            else SetSelectedProcess(state.ProcessName);
        }
        else
        {
            SetSelectedProcess(state.ProcessName);
        }

        await ToggleBoostCommand.ExecuteAsync(null);
    }

    // ── Commands ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanToggleBoost))]
    private async Task ToggleBoostAsync()
    {
        if (CurrentState == AppState.Connected)
            await StopBoostAsync();
        else
            await StartBoostAsync();
    }

    private bool CanToggleBoost() =>
        CurrentState is AppState.Idle or AppState.Connected or AppState.Error;

    // ── Internal Logic ───────────────────────────────────────

    private async Task StartBoostAsync()
    {
        CurrentState = AppState.Connecting;

        // Conflict mitigation (WireGuard for Windows, Hyper-V vms_pp binding...)
        // giờ áp dụng theo vòng đời APP (App.xaml.cs OnLaunched/MainWindow.ExitApp),
        // không còn gắn theo từng lần Start/Stop Boost — xem ConflictDetectionService.

        EngineMode engineMode = SettingsViewModel.LoadEngineMode();

        // Đích đo ping = endpoint của tunnel theo engine mode đang dùng. Phải là IP nằm
        // trong inet4-route-exclude-address, nếu không ICMP sẽ đi qua TUN và bị mihomo
        // GIẢ LẬP (số vô nghĩa) — xem CLAUDE.md mục chẩn đoán.
        // LEGACY: trước đây lấy từ CloudflareNodeService.GetSelectedNode() (đã bỏ).
        _pingMonitor.SetTarget(engineMode == EngineMode.DirectMasqueBeta
            ? "162.159.198.2"     // endpoint MASQUE (API khai: config.peers[0].endpoint.v4)
            : "162.159.192.1");   // endpoint WireGuard

        var exesToBoost = _selectedProfile?.ExecutablesJoined ?? SelectedProcessName;
        if (string.IsNullOrWhiteSpace(exesToBoost)) exesToBoost = "fxgame";

        // Đo baseline & monitor ping theo đúng tiến trình game đang được boost
        _pingMonitor.SetTargetProcess(exesToBoost);
        await _pingMonitor.StartAsync(recordBaseline: true);

        // 1. Chế độ Siêu Tốc (Direct WireGuard) & Direct MASQUE (Beta):
        // Cả hai không cần warp-cli — ngắt nếu đang kết nối để tránh xung đột proxy.
        if (engineMode is EngineMode.DirectWireGuard or EngineMode.DirectMasqueBeta)
        {
            await _warpService.DisconnectAsync();
        }
        else
        {
            // 2. Chế độ Tương Thích (WARP Client Proxy):
            // Yêu cầu app WARP gốc và khởi chạy local proxy 127.0.0.1:40000
            if (!await _warpService.IsInstalledAsync())
            {
                SetError(_loc.ErrWarpNotFound);
                return;
            }

            var connected = await _warpService.ConnectAsync();
            if (!connected)
            {
                SetError(_loc.ErrWarpConnectFail);
                return;
            }
        }

        // Khởi chạy Mihomo Core theo chế độ đã chọn
        if (!string.IsNullOrWhiteSpace(exesToBoost))
        {
            try
            {
                await _mihomoService.StartProxyAsync(exesToBoost, engineMode);
            }
            catch (Exception ex)
            {
                SetError(_loc.ErrMihomoPrefix + ex.Message);
                return;
            }
        }

        CurrentState = AppState.Connected;
        await _networkOptimizer.OptimizeAsync();
        SaveBoostState();
        _ = UpdateRouteTierAsync(engineMode);
        BoostStarted?.Invoke();
    }

    /// <summary>
    /// Hỏi thẳng Cloudflare tài khoản đang dùng (đúng theo engine mode hiện tại)
    /// đã là WARP+ hay chưa, cập nhật label "ROUTE" ở Dashboard — trước đây label
    /// này bị hardcode text "WARP+" trong XAML, luôn hiện sai bất kể tài khoản thật.
    /// </summary>
    private async Task UpdateRouteTierAsync(EngineMode engineMode)
    {
        try
        {
            bool isPlus;
            if (engineMode == EngineMode.DirectMasqueBeta)
            {
                var masqueAcc = await WarpAccountService.GetOrCreateMasqueAccountAsync();
                var status = await WarpAccountService.GetMasqueAccountStatusAsync(masqueAcc);
                isPlus = status?.WarpPlus == true;
            }
            else
            {
                var acc = await WarpAccountService.GetOrCreateAccountAsync();
                var status = await WarpAccountService.GetAccountStatusAsync(acc);
                isPlus = status?.WarpPlus == true;
            }

            RouteTierText = isPlus ? "WARP+" : "WARP Free";
            RouteTierBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isPlus
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 246, 150, 30)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 200, 200, 200));
        }
        catch
        {
            RouteTierText = "—";
        }
    }

    private async Task StopBoostAsync()
    {
        CurrentState = AppState.Disconnecting;
        _pingMonitor.Stop();
        _mihomoService.StopProxy();

        await _networkOptimizer.RestoreAsync();
        await _warpService.ClearSplitTunnelAsync();
        await _warpService.DisconnectAsync();

        CurrentState = AppState.Idle;
        // -1 = "khong do duoc" (khong phai 0 ms / 0% — hai so do co nghia khac han).
        CurrentPingMs = -1;
        PacketLossPercent = -1;
        SaveBoostState();
        BoostStopped?.Invoke();
    }

    private void SaveBoostState()
    {
        BoostStateService.SaveState(
            wasConnected: CurrentState == AppState.Connected,
            processName: SelectedProcessName,
            profileName: _selectedProfile?.Name ?? string.Empty);
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        CurrentState = AppState.Error;
        _pingMonitor.Stop();
        SaveBoostState();
    }

    /// <summary>
    /// RTT/loss o day la tinh TOI EDGE WARP. Chi dang tin khi endpoint cua mode
    /// dang dung NAM TRONG inet4-route-exclude-address — luc do ICMP toi no di
    /// truc tiep ra NIC vat ly.
    ///   - DirectWireGuard  : endpoint 162.159.192.1 duoc loai tru  => DO DUOC
    ///   - DirectMasqueBeta : endpoint 162.159.198.2 duoc loai tru  => DO DUOC
    ///   - WarpClientProxy  : KHONG co endpoint nao duoc loai tru   => ICMP di qua
    ///     TUN va bi mihomo GIA LAP, so vo nghia => hien "không đo được".
    /// SUA 2026-08-22: truoc day chi cho DirectWireGuard, nen sau khi MASQUE thanh
    /// mode mac dinh thi ca 3 o (PING/LOSS/HISTORY) deu tro thanh "—" oan.
    /// </summary>
    private void OnPingUpdated(object? sender, PingStats stats)
    {
        var mode = SettingsViewModel.LoadEngineMode();
        bool edgeMeasurable = mode == Models.EngineMode.DirectWireGuard
                           || mode == Models.EngineMode.DirectMasqueBeta;

        // Coi la "dang loi" neu mihomo bao dial that bai trong 15 giay gan nhat.
        // Nguong nay co y de ngan: mot client retry cung sinh loi, nen day la
        // "co loi ket noi", KHONG phai ket luan tunnel da chet.
        var lastFail = _mihomoService.LastGameDialFailureUtc;
        bool recentFail = lastFail.HasValue &&
                          (DateTime.UtcNow - lastFail.Value) < TimeSpan.FromSeconds(15);

        _dispatcher.TryEnqueue(() =>
        {
            CurrentPingMs     = edgeMeasurable ? stats.CurrentPingMs : -1;
            BaselinePingMs    = stats.BaselinePingMs;
            PacketLossPercent = edgeMeasurable ? stats.PacketLossPercent : -1;
            // Khong ve do thi tu so bi gia lap: cung ly do voi CurrentPingMs o tren.
            PingHistory       = edgeMeasurable
                              ? new List<long>(_pingMonitor.PingHistory)
                              : new List<long>();

            if (CurrentState != AppState.Connected)
            {
                GameLinkText  = "—";
                GameLinkBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 200, 200, 200));
            }
            else if (recentFail)
            {
                GameLinkText  = "SERVER GAME: có lỗi kết nối";
                GameLinkBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 255, 120, 120));
            }
            else
            {
                GameLinkText  = "SERVER GAME: bình thường";
                GameLinkBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 120, 220, 140));
            }
        });
    }

    public void SetSelectedProcess(string processName)
    {
        _selectedProfile    = null;
        SelectedProcessName = processName;
        OnPropertyChanged(nameof(GameDisplayName));

        if (CurrentState == AppState.Connected)
        {
            _ = UpdateActiveProxyRulesAsync();
            SaveBoostState();
        }
    }

    /// <summary>Chọn một Game Profile — hiển thị tên thương mại và boost toàn bộ exe</summary>
    public void SetSelectedProfile(GameProfile profile)
    {
        _selectedProfile    = profile;
        SelectedProcessName = profile.ExecutablesJoined;
        OnPropertyChanged(nameof(GameDisplayName));

        if (CurrentState == AppState.Connected)
        {
            _ = UpdateActiveProxyRulesAsync();
            SaveBoostState();
        }
    }

    private async Task UpdateActiveProxyRulesAsync()
    {
        var exesToBoost = _selectedProfile?.ExecutablesJoined ?? SelectedProcessName;
        if (!string.IsNullOrWhiteSpace(exesToBoost))
        {
            try
            {
                EngineMode engineMode = SettingsViewModel.LoadEngineMode();
                await _mihomoService.StartProxyAsync(exesToBoost, engineMode);
            }
            catch (Exception ex)
            {
                SetError(_loc.ErrMihomoPrefix + ex.Message);
            }
        }
    }

    public async Task HandleAppExitAsync()
    {
        if (CurrentState == AppState.Connected || CurrentState == AppState.Connecting)
        {
            await StopBoostAsync();
        }
    }
}
