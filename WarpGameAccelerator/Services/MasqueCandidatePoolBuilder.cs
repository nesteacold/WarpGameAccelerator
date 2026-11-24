using System.Net;
using System.Net.Sockets;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Dựng danh sách ứng viên MASQUE cho chế độ multi-candidate (GAME-PATH / PROBE-PATH).
/// Hàm thuần (không chạm đĩa/mạng) để kiểm chứng được trong phòng thí nghiệm.
///
/// Khi máy có IPv6 native (đo thực nghiệm 2026-11-24: 12/22 = 54,5% trúng HKG, RTT đi
/// TW 57–75 ms so với >130 ms / fail ở IPv4 SIN):
///   - Dựng 6 slot MASQUE-IPv6 (2606:4700:103::1 và 104::1 trên các cổng 443/500/1701/4500)
///     → xác suất có ≥1 slot HKG > 98,4% (thực tế 3–5 slot HKG).
///   - Giữ thêm 1 slot IPv4 mặc định làm lưới an toàn cuối nếu ISP đột ngột rớt IPv6.
///
/// Khi máy KHÔNG có IPv6:
///   - Không đưa IPv6 vào pool (tránh lỗi dial vô ích), chỉ dùng IPv4 (default + roster/history IPv4).
/// </summary>
public static class MasqueCandidatePoolBuilder
{
    public const int MaxCandidatesWithIpv6 = 7; // 6 MASQUE-IPv6 + 1 IPv4 fallback
    public const int MaxCandidatesIpv4Only = 4;

    /// <summary>6 endpoint MASQUE IPv6 chuẩn đã kiểm chứng thực tế trên mihomo v1.19.29.</summary>
    public static readonly IReadOnlyList<MasqueEndpoint> DefaultIpv6Pool =
    [
        new MasqueEndpoint("2606:4700:103::1", 443,  "h3"),
        new MasqueEndpoint("2606:4700:103::1", 500,  "h3"),
        new MasqueEndpoint("2606:4700:103::1", 1701, "h3"),
        new MasqueEndpoint("2606:4700:103::1", 4500, "h3"),
        new MasqueEndpoint("2606:4700:104::1", 443,  "h3"),
        new MasqueEndpoint("2606:4700:104::1", 1701, "h3"),
    ];

    public static List<MasqueEndpoint> Build(
        MasqueEndpoint defaultEndpoint,
        bool hasNativeIpv6,
        IReadOnlyList<MasqueEndpoint>? userRoster = null,
        IReadOnlyList<MasqueEndpoint>? scanHistory = null)
    {
        var result = new List<MasqueEndpoint>();

        bool TryAdd(MasqueEndpoint ep, int limit)
        {
            if (result.Count >= limit) return false;
            if (!hasNativeIpv6 && IsIpv6(ep.Address)) return false;
            if (result.Any(x => SameEndpoint(x, ep))) return false;
            result.Add(ep);
            return true;
        }

        if (hasNativeIpv6)
        {
            // Dự trữ 1 chỗ cuối cho fallback IPv4 (tối đa 6 IPv6 + 1 IPv4 = 7).
            int ipv6Cap = MaxCandidatesWithIpv6 - 1;

            // 1) Tôn trọng roster người dùng tự ghim (⭐) trước.
            if (userRoster != null)
            {
                foreach (var ep in userRoster)
                    if (IsIpv6(ep.Address)) TryAdd(ep, ipv6Cap);
            }

            // 2) Nếu endpoint mặc định/chọn tay là IPv6, đưa lên đầu danh sách.
            if (IsIpv6(defaultEndpoint.Address) && !result.Any(x => SameEndpoint(x, defaultEndpoint)))
            {
                result.Insert(0, defaultEndpoint);
                if (result.Count > ipv6Cap) result.RemoveAt(result.Count - 1);
            }

            // 3) Điền đủ 6 slot từ bộ IPv6 đã kiểm chứng.
            foreach (var ep in DefaultIpv6Pool)
                TryAdd(ep, ipv6Cap);

            // 4) Luôn gắn 1 dự phòng IPv4 ở cuối (ưu tiên default nếu là IPv4, else roster IPv4, else 162.159.198.2:443).
            MasqueEndpoint ipv4Fallback = !IsIpv6(defaultEndpoint.Address)
                ? defaultEndpoint
                : userRoster?.FirstOrDefault(e => !IsIpv6(e.Address))
                  ?? new MasqueEndpoint("162.159.198.2", 443, "h3");
            TryAdd(ipv4Fallback, MaxCandidatesWithIpv6);
        }
        else
        {
            // Máy chỉ có IPv4: giữ đúng logic an toàn cũ, lọc bỏ mọi endpoint IPv6.
            if (!IsIpv6(defaultEndpoint.Address))
                TryAdd(defaultEndpoint, MaxCandidatesIpv4Only);
            else
                TryAdd(new MasqueEndpoint("162.159.198.2", 443, "h3"), MaxCandidatesIpv4Only);

            if (userRoster != null && userRoster.Count > 0)
            {
                foreach (var ep in userRoster)
                    TryAdd(ep, MaxCandidatesIpv4Only);
            }
            else if (scanHistory != null)
            {
                foreach (var ep in scanHistory)
                    TryAdd(ep, MaxCandidatesIpv4Only);
            }
        }

        return result;
    }

    public static bool IsIpv6(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool SameEndpoint(MasqueEndpoint a, MasqueEndpoint b) =>
        string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Network ?? "h3", b.Network ?? "h3", StringComparison.OrdinalIgnoreCase);
}
