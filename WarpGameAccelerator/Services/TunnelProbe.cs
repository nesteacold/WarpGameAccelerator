// ============================================================
// Services/TunnelProbe.cs
// Mot dong sieu ngan trong log: LUC MO CLIENT thi tunnel dang chay hay khong.
// ============================================================
namespace WarpGameAccelerator.Services;

/// <summary>
/// Lý do tồn tại (batch 56): 124/615 lần "chưa thấy kết nối server" tập trung vào các phiên
/// không có dấu vết tunnel nào, nhưng log cũ KHÔNG ghi trạng thái tunnel lúc mở client ⇒
/// không tách được "thất bại vì đường hầm chưa bật" (hành vi đúng) khỏi "thất bại dù đang boost"
/// (bug mạng thật). Một chuỗi 20 ký tự mỗi lần mở client là đủ để lần đo sau không phải đoán.
/// </summary>
public static class TunnelProbe
{
    public static readonly string[] TunnelProcesses = { "mihomo", "sing-box", "warp_proxy" };

    /// <summary>Ham thuan: `countByName` do tién trình that cung cấp ⇒ kiem chung duoc trong lab.</summary>
    public static string Describe(Func<string, int> countByName)
    {
        var on = new List<string>();
        foreach (var n in TunnelProcesses)
        {
            int c;
            try { c = countByName(n); } catch { c = 0; }
            if (c > 0) on.Add(c == 1 ? n : $"{n}:{c}");
        }
        return on.Count == 0 ? "tunnel=KHONG-chay" : "tunnel=" + string.Join("+", on);
    }

    public static string Current() =>
        Describe(n => System.Diagnostics.Process.GetProcessesByName(n).Length);
}
