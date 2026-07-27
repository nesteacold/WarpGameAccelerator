// ============================================================
// Services/LocalizationService.cs — Đa ngôn ngữ VIE / ENG
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public enum AppLanguage { VI, EN }

/// <summary>
/// Singleton service quản lý toàn bộ chuỗi UI đa ngôn ngữ.
/// Khi đổi ngôn ngữ, raise PropertyChanged("") để mọi x:Bind tự cập nhật.
/// </summary>
public partial class LocalizationService : ObservableObject
{
    private static readonly string _settingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "language.json");

    [ObservableProperty] private AppLanguage _currentLanguage;

    public LocalizationService()
    {
        _currentLanguage = LoadLanguage();
    }

    public bool IsVietnamese => CurrentLanguage == AppLanguage.VI;

    partial void OnCurrentLanguageChanged(AppLanguage value)
    {
        SaveLanguage(value);
        // Thông báo toàn bộ string property đã thay đổi (null = tất cả)
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(null));
    }

    // ── Helpers ─────────────────────────────────────────────

    private static AppLanguage LoadLanguage()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var doc  = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("language", out var el))
                    return el.GetString() == "EN" ? AppLanguage.EN : AppLanguage.VI;
            }
        }
        catch { }
        return AppLanguage.VI;
    }

    private static void SaveLanguage(AppLanguage lang)
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(new { language = lang.ToString() }));
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════
    // CÁC CHUỖI UI — đặt theo nhóm chức năng
    // ══════════════════════════════════════════════════════════

    // ── Dashboard ────────────────────────────────────────────
    public string DashProcessBeingBoosted => VI("Process đang được boost",  "Process being boosted");
    public string DashChange              => VI("Đổi",                       "Change");
    public string DashBefore             => VI("Trước: ",                    "Before: ");
    public string DashBoostNow           => VI("BOOST NOW",                  "BOOST NOW");
    public string DashStop               => VI("STOP",                       "STOP");
    public string DashConnecting         => VI("Đang kết nối...",            "Connecting...");
    public string DashDisconnecting      => VI("Đang ngắt...",               "Disconnecting...");
    public string DashRetry              => VI("THỬ LẠI",                    "RETRY");
    public string DashTooltipBoost       => VI("Bắt đầu tăng tốc WARP+",   "Start WARP+ acceleration");
    public string DashStatusReady        => VI("Sẵn sàng tăng tốc",         "Ready to accelerate");
    public string DashStatusConnecting   => VI("Đang kết nối WARP+...",      "Connecting to WARP+...");
    public string DashStatusActive       => VI("🚀 WARP+ đang hoạt động",    "🚀 WARP+ is active");
    public string DashAutoRegion         => VI("Auto Region",                 "Auto Region");
    public string DashNoGameSelected     => VI("Chưa chọn game",              "No game selected");

    // ── Picker ───────────────────────────────────────────────
    public string PickerTitle            => VI("Chọn Game",                  "Select Game");
    public string PickerSubtitle         => VI("Chỉ process được chọn sẽ đi qua WARP+",
                                               "Only selected process routes through WARP+");
    public string PickerTabProfiles      => VI("Profiles",                   "Profiles");
    public string PickerTabProcess       => VI("Process",                    "Process");
    public string PickerSearchPlaceholder=> VI("Tìm process...",             "Search process...");
    public string PickerBtnRefresh       => VI("🔄  Làm mới",               "🔄  Refresh");
    public string PickerBtnBrowse        => VI("📂  Duyệt file",            "📂  Browse");
    public string PickerBtnSelect        => VI("✅  Chọn",                   "✅  Select");
    public string PickerLoading          => VI("Đang tải processes...",      "Loading processes...");
    public string PickerInfoBanner       => VI("Split Tunneling: chỉ process được chọn đi qua WARP+. Chrome, Discord, YouTube giữ nguyên mạng gốc.",
                                               "Split Tunneling: only selected process routes through WARP+. Chrome, Discord, YouTube use your normal network.");

    // ── Settings ─────────────────────────────────────────────
    public string SettSectionStartup     => VI("KHỞI ĐỘNG",                 "STARTUP");
    public string SettAutoStartTitle     => VI("Tự động khởi động cùng Windows", "Auto-start with Windows");
    public string SettSectionPing        => VI("PING MONITOR",              "PING MONITOR");
    public string SettPingServerTitle    => VI("Server đo Ping",            "Ping Server");
    public string SettPingServerSubtitle => VI("Chọn IP server để theo dõi độ trễ", "Select IP server to monitor latency");
    public string SettPingDeleteTooltip  => VI("Xóa Server này",            "Remove this server");
    public string SettAddServerTitle     => VI("Thêm Server mới",           "Add new server");
    public string SettNamePlaceholder    => VI("Tên (VD: Game VN)",         "Name (e.g.: Game Server)");
    public string SettIpPlaceholder      => VI("IP (VD: 8.8.8.8)",         "IP (e.g.: 8.8.8.8)");
    public string SettBtnTestIp          => VI("Test IP",                   "Test IP");
    public string SettBtnAdd             => VI("+ Thêm",                    "+ Add");
    public string SettSectionWarp        => VI("CLOUDFLARE WARP",           "CLOUDFLARE WARP");
    public string SettWarpClientSubtitle => VI("Mở trang tải về WARP client", "Download WARP client page");
    public string SettBtnDownload        => VI("Tải về →",                  "Download →");
    public string SettSectionLanguage    => VI("NGÔN NGỮ",                  "LANGUAGE");
    public string SettLangTitle          => VI("Ngôn ngữ giao diện",        "Display language");
    public string SettLangSubtitle       => VI("Thay đổi có hiệu lực ngay lập tức", "Changes take effect immediately");
    public string SettSectionAbout       => VI("VỀ ỨNG DỤNG",              "ABOUT");
    public string SettBtnCheckUpdate     => VI("Kiểm tra bản cập nhật",     "Check for updates");
    public string SettUpdateChecking     => VI("Đang kiểm tra...",          "Checking...");
    public string SettUpdateLatest       => VI("Bạn đang dùng bản mới nhất!", "You are on the latest version!");
    public string SettUpdateAvailable    => VI("Có bản cập nhật mới!",      "Update available!");
    public string SettUpdateError        => VI("Lỗi kiểm tra cập nhật",     "Error checking for updates");
    public string SettUpdateDownloading  => VI("Đang tải & Cài đặt...",     "Downloading & Installing...");

    // ── Connection Engine Mode ────────────────────────────────
    public string SettSectionEngine      => VI("CHẾ ĐỘ KẾT NỐI (ENGINE)", "CONNECTION MODE (ENGINE)");
    public string SettEngineDirectTitle  => VI("Game Mode (Direct WireGuard) 🔥 Khuyên dùng", "Game Mode (Direct WireGuard) 🔥 Recommended");
    public string SettEngineDirectBadge  => VI("(Khuyên dùng)", "(Recommended)");
    public string SettEngineDirectDesc   => VI("Khuyên dùng cho Game thời gian thực. Tối ưu Ping, chống rớt mạng, không cần cài app WARP.",
                                               "Recommended for real-time games. Lowest ping, persistent connection, no WARP app needed.");
    public string SettEngineWarpTitle    => VI("Chế độ Tương Thích (WARP Client)", "WARP Client Proxy (Compatibility Mode)");
    public string SettEngineWarpDesc     => VI("Dành cho duyệt Web / App thông thường. Yêu cầu ứng dụng Cloudflare WARP gốc.",
                                               "For general browsing & apps. Requires official Cloudflare WARP app.");

    // ── Update Dialog ────────────────────────────────────────
    public string UpdateDialogTitle      => VI("Có Phiên Bản Mới",          "Update Available");
    public string UpdateDialogBtnUpdate  => VI("Cập nhật ngay",             "Update Now");
    public string UpdateDialogBtnLater   => VI("Để sau",                    "Later");

    // ── Navigation ───────────────────────────────────────────
    public string NavSelectGame          => VI("Chọn Game",                 "Select Game");
    public string NavSettings            => VI("Cài đặt",                   "Settings");
    public string NavExit                => VI("Thoát (Exit)",              "Exit");

    // ── Exit Dialog ──────────────────────────────────────────
    public string ExitTitle              => VI("Xác nhận thoát",            "Confirm Exit");
    public string ExitMessage            => VI("Bạn có chắc chắn muốn thoát hoàn toàn WARP Game Accelerator? Mọi tiến trình đang được Boost sẽ bị ngắt kết nối khỏi Cloudflare WARP.",
                                               "Are you sure you want to completely exit WARP Game Accelerator? All boosted processes will be disconnected from Cloudflare WARP.");
    public string ExitBtnExit            => VI("Thoát",                     "Exit");
    public string ExitBtnMinimize        => VI("Thu nhỏ",                   "Minimize");
    public string ExitBtnCancel          => VI("Hủy",                       "Cancel");
    public string TrayMinimizedMsg       => VI("Ứng dụng vẫn đang chạy trong khay hệ thống.", "App is still running in the system tray.");

    // ── Error Messages ───────────────────────────────────────
    public string ErrWarpNotFound        => VI("Không tìm thấy warp-cli. Hãy cài Cloudflare WARP trước.",
                                               "warp-cli not found. Please install Cloudflare WARP first.");
    public string ErrWarpConnectFail     => VI("Không thể kết nối WARP. Kiểm tra WARP client.",
                                               "Cannot connect to WARP. Check WARP client.");
    public string ErrMihomoPrefix        => VI("Lỗi khởi chạy Mihomo Core: ", "Error starting Mihomo Core: ");

    // ── Helper ───────────────────────────────────────────────
    private string VI(string vi, string en) =>
        CurrentLanguage == AppLanguage.VI ? vi : en;
}
