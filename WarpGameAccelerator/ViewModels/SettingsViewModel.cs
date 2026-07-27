// ============================================================
// ViewModels/SettingsViewModel.cs — Cài đặt ứng dụng
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarpGameAccelerator.Helpers;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.Storage;

namespace WarpGameAccelerator.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PingMonitorService _pingMonitor;
    private readonly LocalizationService _loc;
    private readonly UpdateService _updateService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoStartToggleLabel))]
    private bool _autoStartEnabled;

    [ObservableProperty] private PingTarget?  _selectedPingTarget;
    [ObservableProperty] private ObservableCollection<PingTarget> _pingTargets = new();
    [ObservableProperty] private string       _appVersion         = GetVersion();

    // Connection Engine Mode (Direct WireGuard vs WARP Client Proxy)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWarpClientProxy))]
    private bool _isDirectWireGuard = LoadEngineMode();

    public bool IsWarpClientProxy
    {
        get => !IsDirectWireGuard;
        set => IsDirectWireGuard = !value;
    }

    public LocalizationService Loc => _loc;
    public bool IsVietnamese => _loc.CurrentLanguage == AppLanguage.VI;

    // Thuộc tính cho phần Thêm IP mới
    [ObservableProperty] private string _newTargetName = string.Empty;
    [ObservableProperty] private string _newTargetHost = string.Empty;
    [ObservableProperty] private string _newTargetTestResult = string.Empty;

    // Update state
    [ObservableProperty] private string _updateStatusText = string.Empty;
    private string _latestDownloadUrl = string.Empty;
    [ObservableProperty] private bool   _isTestingPing = false;

    public string AutoStartToggleLabel =>
        AutoStartEnabled ? "Tự khởi động cùng Windows (BẬT)" : "Tự khởi động cùng Windows (TẮT)";

    public SettingsViewModel(PingMonitorService pingMonitor, LocalizationService loc)
    {
        _pingMonitor = pingMonitor;
        _loc         = loc;
        _updateService = new UpdateService();
        _autoStartEnabled = StartupHelper.IsAutoStartEnabled();
        LoadPingTargets();
        _selectedPingTarget = PingTargets.FirstOrDefault() ?? PingTarget.Defaults[0];

        _loc.PropertyChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(IsVietnamese));
            OnPropertyChanged(nameof(Loc));
        };
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatusText = _loc.SettUpdateChecking;
        var (hasUpdate, version, downloadUrl) = await _updateService.CheckForUpdateAsync();

        if (hasUpdate)
        {
            UpdateStatusText = _loc.SettUpdateAvailable;
            _latestDownloadUrl = downloadUrl;
            
            // Start download right away from settings
            UpdateStatusText = _loc.SettUpdateDownloading;
            await _updateService.DownloadAndInstallUpdateAsync(_latestDownloadUrl);
        }
        else if (string.IsNullOrEmpty(version))
        {
            UpdateStatusText = _loc.SettUpdateError;
        }
        else
        {
            UpdateStatusText = _loc.SettUpdateLatest;
        }
    }

    private readonly string _settingsFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "ping_targets.json");

    private void LoadPingTargets()
    {
        if (System.IO.File.Exists(_settingsFilePath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<List<PingTarget>>(json);
                if (loaded != null && loaded.Count > 0)
                {
                    foreach (var item in loaded) PingTargets.Add(item);
                    return;
                }
            }
            catch { /* fallback */ }
        }
        
        foreach (var def in PingTarget.Defaults)
            PingTargets.Add(def);
    }

    private void SavePingTargets()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_settingsFilePath)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(PingTargets.ToList());
            System.IO.File.WriteAllText(_settingsFilePath, json);
        }
        catch { }
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (value) StartupHelper.EnableAutoStart();
        else       StartupHelper.DisableAutoStart();
    }

    partial void OnIsDirectWireGuardChanged(bool value)
    {
        SaveEngineMode(value);
    }

    private static readonly string _engineModeFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "engine_mode.json");

    public static bool LoadEngineMode()
    {
        try
        {
            if (System.IO.File.Exists(_engineModeFilePath))
            {
                var json = System.IO.File.ReadAllText(_engineModeFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("isDirectWireGuard", out var el))
                    return el.GetBoolean();
            }
        }
        catch { }
        return true; // Default: Direct WireGuard
    }

    private static void SaveEngineMode(bool isDirectWireGuard)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_engineModeFilePath)!;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { isDirectWireGuard });
            System.IO.File.WriteAllText(_engineModeFilePath, json);
        }
        catch { }
    }

    partial void OnSelectedPingTargetChanged(PingTarget? value)
    {
        if (value != null)
        {
            _pingMonitor.SetTarget(value.Host);
        }
    }

    [RelayCommand]
    private async Task TestPingAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTargetHost)) return;
        IsTestingPing = true;
        NewTargetTestResult = "Đang đo...";
        
        long ms = await _pingMonitor.MeasurePingAsync(NewTargetHost);
        if (ms >= 0) NewTargetTestResult = $"{ms} ms";
        else NewTargetTestResult = "Lỗi (Timeout)";
        
        IsTestingPing = false;
    }

    [RelayCommand]
    private void AddPingTarget()
    {
        if (string.IsNullOrWhiteSpace(NewTargetName) || string.IsNullOrWhiteSpace(NewTargetHost))
            return;

        PingTargets.Add(new PingTarget { Name = NewTargetName, Host = NewTargetHost });
        SavePingTargets();

        NewTargetName = string.Empty;
        NewTargetHost = string.Empty;
        NewTargetTestResult = string.Empty;
    }

    [RelayCommand]
    private void RemovePingTarget(PingTarget? target)
    {
        if (target != null && target.Host != "1.1.1.1")
        {
            PingTargets.Remove(target);
            SavePingTargets();
            if (SelectedPingTarget == null || SelectedPingTarget == target)
            {
                SelectedPingTarget = PingTargets.FirstOrDefault() ?? PingTarget.Defaults[0];
            }
        }
    }

    [RelayCommand]
    private static void OpenCloudflareWarp()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "https://1.1.1.1",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void SetLanguageVi() => _loc.CurrentLanguage = AppLanguage.VI;

    [RelayCommand]
    private void SetLanguageEn() => _loc.CurrentLanguage = AppLanguage.EN;

    private static string GetVersion()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
    }
}
