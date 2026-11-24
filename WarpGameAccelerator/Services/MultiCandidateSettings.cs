using System.Text.Json;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Feature gate cho tính năng THỬ NGHIỆM "nhiều tunnel MASQUE dự phòng, chuyển
/// candidate đang mang traffic game bằng REST API của mihomo thay vì rebuild toàn
/// bộ process" (xem MihomoService's DirectMasqueBeta useCandidateGroups branch,
/// MihomoControlClient, MasqueTunnelSweepService).
///
/// MẶC ĐỊNH TẮT — MASQUE hiện là engine mode mặc định của cả app, và tính năng này
/// CHƯA được kiểm chứng end-to-end với traffic game thật (xem plan). Idiom lưu file
/// JSON đơn giản dưới Data\ giống hệt engine_mode.json (xem SettingsViewModel
/// LoadEngineMode/SaveEngineMode).
/// </summary>
public static class MultiCandidateSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "multi_candidate_masque.json");

    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("enabled", out var el))
                return el.GetBoolean();
        }
        catch
        {
            // File hỏng/không đọc được — coi như tắt (an toàn hơn bật nhầm).
        }
        return false;
    }

    /// <summary>
    /// Tự động săn và chọn tuyến HKG/TPE tốt nhất ngay khi bật Boost (mặc định BẬT khi multi-candidate bật).
    /// </summary>
    public static bool IsAutoHuntHkgEnabled()
    {
        if (!IsEnabled()) return false;
        try
        {
            if (!File.Exists(FilePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("autoHuntHkg", out var el))
                return el.GetBoolean();
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Tự động chuyển sang tuyến HKG dự phòng (Hot-Standby Failover) khi tuyến chính gặp lỗi dial
    /// hoặc mất HKG mà không restart Mihomo (mặc định BẬT khi multi-candidate bật).
    /// </summary>
    public static bool IsAutoFailoverEnabled()
    {
        if (!IsEnabled()) return false;
        try
        {
            if (!File.Exists(FilePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("autoFailover", out var el))
                return el.GetBoolean();
        }
        catch { }
        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            bool autoHunt = true;
            bool autoFailover = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                    if (doc.RootElement.TryGetProperty("autoHuntHkg", out var ah)) autoHunt = ah.GetBoolean();
                    if (doc.RootElement.TryGetProperty("autoFailover", out var af)) autoFailover = af.GetBoolean();
                }
            }
            catch { }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { enabled, autoHuntHkg = autoHunt, autoFailover }));
        }
        catch
        {
            // Ghi lỗi thì giữ nguyên trạng thái cũ trong bộ nhớ; không chặn UI.
        }
    }
}
