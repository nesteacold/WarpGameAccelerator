// ============================================================
// Models/PingStats.cs — Thống kê ping real-time
// ============================================================
namespace WarpGameAccelerator.Models;

public class PingStats
{
    public long CurrentPingMs { get; set; }
    public long BaselinePingMs { get; set; }
    public double PacketLossPercent { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Cải thiện ping so với baseline (ms, dương = tốt hơn)</summary>
    public long Improvement => BaselinePingMs - CurrentPingMs;

    public bool IsGood => CurrentPingMs < 80 && PacketLossPercent < 1.0;
}

public class PingTarget
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;

    public override string ToString() => $"{Name} ({Host})";

    public static readonly PingTarget[] Defaults =
    [
        new PingTarget { Name = "Cloudflare (Baseline)", Host = "1.1.1.1" },
        new PingTarget { Name = "AoW TW — Server 1",    Host = "103.197.172.27" },
        new PingTarget { Name = "AoW TW — Server 2",    Host = "103.197.172.23" },
    ];
}
