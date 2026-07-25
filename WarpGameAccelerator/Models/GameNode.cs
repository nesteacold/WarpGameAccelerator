// ============================================================
// Models/GameNode.cs
// Model đại diện cho một Node Server Cloudflare WARP
// ============================================================
namespace WarpGameAccelerator.Models;

public class GameNode
{
    public string Id          { get; set; } = string.Empty; // VD: "auto", "tw_tpe01", "vn_hcm01"
    public string Name        { get; set; } = string.Empty; // VD: "Auto (Khuyên dùng)", "Taiwan (Taipei 01)"
    public string Flag        { get; set; } = string.Empty; // VD: "⚡", "🇹🇼", "🇻🇳", "🇭🇰"
    public string Route       { get; set; } = string.Empty; // VD: "Tự chọn tốt nhất", "VN (SGN) ➔ Taiwan (TPE)"
    public string EndpointIp  { get; set; } = string.Empty; // IP Endpoint Cloudflare (VD: "162.159.192.1")
    public int    Port        { get; set; } = 2408;
    public int    PingMs      { get; set; } = -1;           // -1: Chưa đo, 9999: Timeout
    public bool   IsAuto      { get; set; } = false;
    public bool   IsSelected  { get; set; } = false;

    // Helper hiển thị chuỗi Ping trên UI
    public string PingDisplay => PingMs switch
    {
        < 0    => "Đang đo...",
        >= 999 => "Timeout 🔴",
        < 45   => $"{PingMs} ms 🟢",
        < 80   => $"{PingMs} ms 🟡",
        _      => $"{PingMs} ms 🔴"
    };
}
