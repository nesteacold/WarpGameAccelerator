// ============================================================
// Services/ConflictDetectionService.cs
// Điểm gom duy nhất cho các "conflict" đã biết (driver TUN/WFP khác cùng
// chạy gây xung đột với Mihomo) — mỗi conflict có toggle bật/tắt riêng của
// người dùng (mặc định BẬT = tự động xử lý), lưu ở conflict_toggles.json.
//
// BẬT (IsEnabled=true) = tự tắt conflict lúc Start Boost, tự khôi phục lúc
// Stop Boost (hành vi mặc định, giữ đúng như trước khi có toggle).
// TẮT = bỏ qua, không đụng vào — dành cho người dùng biết rõ mình cần giữ
// nguyên (vd VM Hyper-V khác cần binding đó).
//
// Danh sách hiện có:
//   - WireGuardForWindows: bọc lại WireGuardConflictGuard có sẵn.
//   - HyperVVmsBinding: HyperVConflictGuard (mới) — xem CLAUDE.md mục
//     "Hyper-V xung đột với TUN".
// ============================================================
using System.IO;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public sealed record ConflictItemInfo(string Id, string DisplayName, string Description);

public static class ConflictDetectionService
{
    public const string WireGuardForWindowsId = "WireGuardForWindows";
    public const string HyperVVmsBindingId    = "HyperVVmsBinding";

    private static readonly string TogglesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "conflict_toggles.json");

    public static readonly IReadOnlyList<ConflictItemInfo> Items = new List<ConflictItemInfo>
    {
        new(WireGuardForWindowsId, "WireGuard for Windows",
            "Tạm dừng service WireGuardTunnel$* (VPN remote-access cá nhân) lúc Boost — driver TUN của nó có thể xung đột với Mihomo."),
        new(HyperVVmsBindingId, "Hyper-V Virtual Switch binding",
            "Tạm tắt binding NDIS Hyper-V trên card mạng lúc Boost — đã xác nhận là nguyên nhân gây ping timeout/rớt client khi chạy cùng Mihomo TUN."),
    };

    /// <summary>Đọc toggle của 1 conflict, mặc định true nếu chưa từng lưu.</summary>
    public static bool IsEnabled(string id)
    {
        var all = LoadToggles();
        return !all.TryGetValue(id, out var value) || value;
    }

    public static void SetEnabled(string id, bool value)
    {
        var all = LoadToggles();
        all[id] = value;
        SaveToggles(all);
    }

    /// <summary>Trạng thái phát hiện hiện tại (để hiển thị UI), không tự sửa gì.</summary>
    public static async Task<bool> IsDetectedAsync(string id) => id switch
    {
        WireGuardForWindowsId => (await WireGuardConflictGuard.GetAvailableTunnelNamesAsync()).Count > 0,
        HyperVVmsBindingId    => await HyperVConflictGuard.IsDetectedAsync(),
        _ => false,
    };

    /// <summary>Gọi lúc Start Boost — chỉ mitigate các conflict đang bật toggle.</summary>
    public static async Task MitigateEnabledAsync()
    {
        if (IsEnabled(WireGuardForWindowsId))
            await WireGuardConflictGuard.PauseAsync();

        if (IsEnabled(HyperVVmsBindingId))
            await HyperVConflictGuard.PauseAsync();
    }

    /// <summary>Gọi lúc Stop Boost — luôn thử khôi phục cả 2 (mỗi guard tự no-op nếu không có gì để trả lại), tránh kẹt conflict ở trạng thái tắt nếu người dùng đổi toggle giữa chừng phiên Boost.</summary>
    public static async Task RestoreAllAsync()
    {
        await WireGuardConflictGuard.ResumeAsync();
        await HyperVConflictGuard.ResumeAsync();
    }

    private static Dictionary<string, bool> LoadToggles()
    {
        try
        {
            if (!File.Exists(TogglesFilePath)) return new Dictionary<string, bool>();
            var json = File.ReadAllText(TogglesFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? new Dictionary<string, bool>();
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    private static void SaveToggles(Dictionary<string, bool> toggles)
    {
        try
        {
            var dir = Path.GetDirectoryName(TogglesFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TogglesFilePath, JsonSerializer.Serialize(toggles));
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[ConflictDetectionService] Lưu toggle lỗi: {ex.Message}");
        }
    }
}
