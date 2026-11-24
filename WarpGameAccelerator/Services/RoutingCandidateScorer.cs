namespace WarpGameAccelerator.Services;

/// <summary>
/// Bộ chấm điểm và quyết định chọn/chuyển tuyến thuần (không I/O, không mạng) cho
/// Auto-Hunt HKG (Bước 2) và Hot-Standby Failover (Bước 3).
///
/// Nguyên tắc:
///   - Chỉ dùng dữ liệu ĐO THẬT (Colo != null). Không nội suy, không dùng Random.
///   - Ưu tiên vùng gần server game Đài Loan: TPE/KHH/HKG (Tier 3) > khu vực Đông Nam Á/Đông Á (Tier 2) > khác (Tier 1).
///   - Trong cùng tier: ưu tiên TargetRttMs (1-RTT tới đích TW/HK thật) thấp nhất, rồi mới tới LatencyMs (Cloudflare trace).
///   - Chống flapping (hysteresis): không nhảy giữa 2 slot cùng tier HKG/TPE chỉ vì lệch vài ms.
/// </summary>
public static class RoutingCandidateScorer
{
    public const double SameTierSwitchMarginMs = 25.0;

    /// <summary>Xếp bậc vùng địa lý theo vị trí gần server game Taiwan (103.197.172.0/24).</summary>
    public static int ColoTier(string? colo)
    {
        if (string.IsNullOrWhiteSpace(colo)) return 0;
        return colo.Trim().ToUpperInvariant() switch
        {
            "TPE" or "KHH" or "HKG" or "MFM" => 3, // Đài Loan / Hồng Kông / Ma Cao — tuyến ngắn nhất
            "SGN" or "HAN" or "SIN" or "NRT" or "KIX" or "ICN" or "MNL" => 2, // Đông Á / Đông Nam Á
            _ => 1
        };
    }

    public static bool IsPreferredRegion(string? colo) => ColoTier(colo) >= 3;

    /// <summary>Độ trễ đại diện để so sánh trong cùng tier: ưu tiên 1-RTT đích thật (TargetRttMs) hơn trace HTTP.</summary>
    public static double EffectiveLatencyMs(CandidateColoStatus s) =>
        s.TargetRttMs is > 0 ? s.TargetRttMs.Value
        : s.LatencyMs is > 0 ? s.LatencyMs.Value
        : double.MaxValue;

    /// <summary>
    /// Chọn candidate tốt nhất trong danh sách đã đo. Có thể loại trừ <paramref name="excludeProxyName"/>
    /// khi cần tìm tuyến dự phòng để failover khỏi tuyến đang gặp lỗi.
    /// </summary>
    public static CandidateColoStatus? PickBest(
        IEnumerable<CandidateColoStatus> candidates,
        string? excludeProxyName = null)
    {
        return candidates
            .Where(s => !string.IsNullOrEmpty(s.Colo))
            .Where(s => excludeProxyName == null || !string.Equals(s.ProxyName, excludeProxyName, StringComparison.Ordinal))
            .OrderByDescending(s => ColoTier(s.Colo))
            .ThenBy(EffectiveLatencyMs)
            .ThenBy(s => s.ProxyName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Quyết định có nên chuyển từ <paramref name="current"/> sang <paramref name="best"/>
    /// trong lần Auto-Hunt đầu phiên hay không.
    /// </summary>
    public static bool ShouldUpgradeOnAutoHunt(CandidateColoStatus? current, CandidateColoStatus? best)
    {
        if (best == null || string.IsNullOrEmpty(best.Colo)) return false;
        if (current == null || string.IsNullOrEmpty(current.Colo)) return true;
        if (string.Equals(current.ProxyName, best.ProxyName, StringComparison.Ordinal)) return false;

        int curTier = ColoTier(current.Colo);
        int bestTier = ColoTier(best.Colo);
        if (bestTier > curTier) return true;
        if (bestTier < curTier) return false;

        // Cùng tier: chỉ đổi nếu nhanh hơn rõ rệt (>= SameTierSwitchMarginMs).
        double curMs = EffectiveLatencyMs(current);
        double bestMs = EffectiveLatencyMs(best);
        return curMs - bestMs >= SameTierSwitchMarginMs;
    }

    /// <summary>
    /// Quyết định có nên chuyển tuyến khi đang trong vòng giám sát Hot-Standby Failover hay không.
    /// </summary>
    public static bool ShouldFailover(
        CandidateColoStatus? current,
        bool currentUnhealthy,
        CandidateColoStatus? standby,
        DateTimeOffset nowUtc,
        DateTimeOffset lastSwitchUtc,
        TimeSpan cooldown)
    {
        if (standby == null || string.IsNullOrEmpty(standby.Colo)) return false;
        if (current != null && string.Equals(current.ProxyName, standby.ProxyName, StringComparison.Ordinal))
            return false;
        if (nowUtc - lastSwitchUtc < cooldown) return false;

        // Tuyến hiện tại hỏng (mất colo / lỗi dial dồn dập) → chuyển ngay sang standby khoẻ.
        if (currentUnhealthy || current == null || string.IsNullOrEmpty(current.Colo))
            return true;

        // Tuyến hiện tại bị trôi ra khỏi vùng HKG/TPE trong khi có standby HKG/TPE khoẻ → nâng cấp.
        return ColoTier(standby.Colo) > ColoTier(current.Colo);
    }
}
