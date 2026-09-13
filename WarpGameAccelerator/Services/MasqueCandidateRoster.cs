using System.Text.Json;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Danh sách endpoint MASQUE người dùng CHỦ ĐỘNG chọn (⭐ ở bảng "Đo & chọn endpoint
/// khác") để giữ sống song song làm ứng viên GAME-PATH/PROBE-PATH — thay cho việc
/// MihomoService tự động lấy 4 kết quả thành công gần nhất trong lịch sử quét mà
/// người dùng không kiểm soát được là endpoint nào. Tối đa 4 (giới hạn tài nguyên
/// concurrent QUIC, xem CLAUDE.md/plan). Rỗng thì MihomoService rơi về hành vi cũ
/// (tự lấy từ lịch sử quét) — file này không bắt buộc phải tồn tại.
/// </summary>
public static class MasqueCandidateRoster
{
    private const int MaxCount = 4;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpGameAccelerator", "Data");
    private static readonly string RosterPath = Path.Combine(DataDir, "masque_candidate_roster.json");

    public static List<MasqueEndpoint> Load()
    {
        try
        {
            if (!File.Exists(RosterPath)) return new List<MasqueEndpoint>();
            var list = JsonSerializer.Deserialize<List<MasqueEndpoint>>(File.ReadAllText(RosterPath));
            return list ?? new List<MasqueEndpoint>();
        }
        catch
        {
            // File hỏng/không đọc được — coi như chưa chọn gì, KHÔNG chặn Boost.
            return new List<MasqueEndpoint>();
        }
    }

    private static void Save(List<MasqueEndpoint> roster)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(RosterPath + ".tmp", JsonSerializer.Serialize(roster));
        File.Move(RosterPath + ".tmp", RosterPath, true);
    }

    private static bool SameEndpoint(MasqueEndpoint a, MasqueEndpoint b) =>
        a.Address == b.Address && a.Port == b.Port && a.Network == b.Network;

    public static bool Contains(MasqueEndpoint endpoint) => Load().Any(e => SameEndpoint(e, endpoint));

    /// <summary>Bật/tắt 1 endpoint trong roster. Trả về roster mới sau khi đổi.
    /// Không thêm quá <see cref="MaxCount"/> — gọi khi đã đủ 4 và chưa có sẵn thì bỏ qua.</summary>
    public static List<MasqueEndpoint> Toggle(MasqueEndpoint endpoint)
    {
        var roster = Load();
        int idx = roster.FindIndex(e => SameEndpoint(e, endpoint));
        if (idx >= 0) roster.RemoveAt(idx);
        else if (roster.Count < MaxCount) roster.Add(endpoint);
        Save(roster);
        return roster;
    }
}
