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
    public string PingDisplay      => CurrentPingMs > 0   ? $"{CurrentPingMs} ms" : "-- ms";
    public string BaselineDisplay  => BaselinePingMs > 0  ? $"{BaselinePingMs} ms" : "-- ms";
    public string LossDisplay      => $"{PacketLossPercent:F1} %";

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
                              DispatcherQueue dispatcher)
    {
        _warpService   = warpService;
        _pingMonitor   = pingMonitor;
        _mihomoService = mihomoService;
        _loc           = loc;
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

        // Kiểm tra warp-cli
        if (!await _warpService.IsInstalledAsync())
        {
            SetError(_loc.ErrWarpNotFound);
            return;
        }

        // Đo baseline trước khi connect
        await _pingMonitor.StartAsync(recordBaseline: true);

        // Kết nối WARP (Proxy)
        var connected = await _warpService.ConnectAsync();
        if (!connected)
        {
            SetError(_loc.ErrWarpConnectFail);
            return;
        }

        // Nếu có chọn game, khởi chạy Mihomo với Chế độ Engine được chọn trong Cài đặt
        var exesToBoost = _selectedProfile?.ExecutablesJoined ?? SelectedProcessName;
        if (!string.IsNullOrWhiteSpace(exesToBoost))
        {
            try
            {
                bool isDirectWireGuard = SettingsViewModel.LoadEngineMode();
                await _mihomoService.StartProxyAsync(exesToBoost, isDirectWireGuard);
            }
            catch (Exception ex)
            {
                SetError(_loc.ErrMihomoPrefix + ex.Message);
                return;
            }
        }

        CurrentState = AppState.Connected;
        await _networkOptimizer.OptimizeAsync();
        BoostStarted?.Invoke();
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
        CurrentPingMs = 0;
        PacketLossPercent = 0;
        BoostStopped?.Invoke();
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        CurrentState = AppState.Error;
        _pingMonitor.Stop();
    }

    private void OnPingUpdated(object? sender, PingStats stats)
    {
        _dispatcher.TryEnqueue(() =>
        {
            CurrentPingMs     = stats.CurrentPingMs;
            BaselinePingMs    = stats.BaselinePingMs;
            PacketLossPercent = stats.PacketLossPercent;
            PingHistory       = new List<long>(_pingMonitor.PingHistory);
        });
    }

    public void SetSelectedProcess(string processName)
    {
        _selectedProfile    = null;
        SelectedProcessName = processName;
        OnPropertyChanged(nameof(GameDisplayName));
    }

    /// <summary>Chọn một Game Profile — hiển thị tên thương mại và boost toàn bộ exe</summary>
    public void SetSelectedProfile(GameProfile profile)
    {
        _selectedProfile    = profile;
        SelectedProcessName = profile.ExecutablesJoined;
        OnPropertyChanged(nameof(GameDisplayName));
    }

    public async Task HandleAppExitAsync()
    {
        if (CurrentState == AppState.Connected || CurrentState == AppState.Connecting)
        {
            await StopBoostAsync();
        }
    }
}
