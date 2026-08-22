// ============================================================
// LEGACY — KHÔNG CÒN DÙNG (đánh dấu 2026-08-22). Giữ lại để tham khảo.
//
// VÌ SAO BỎ: chức năng này hứa "chọn node theo vùng" (Taiwan / Hong Kong /
// Singapore) nhưng điều đó BẤT KHẢ THI về nguyên lý. Mọi endpoint WARP đều là
// địa chỉ ANYCAST: cùng một IP được quảng bá từ mọi PoP Cloudflare, và PoP nào
// nhận gói do BGP của ISP quyết định — client không có tiếng nói.
//
// ĐÃ KIỂM 13 TỔ HỢP, mỗi cái dựng tunnel WireGuard THẬT rồi đọc `colo` từ
// https://1.1.1.1/cdn-cgi/trace (bàn thử cô lập: mihomo không có section `tun`,
// nên không thể làm mất mạng máy):
//   - 4 IP  (162.159.192.1 / .192.6 / .195.1 / 188.114.96.1) x cổng 2408  -> SIN
//   - 4 cổng (2408 / 500 / 1701 / 4500)                                    -> SIN
//   - 2 endpoint IPv6 (2606:4700:d0::a29f:c006 / ...c001)                  -> SIN
//   - 2 IP mới do API Cloudflare tự trả về khi đăng ký                     -> SIN
//   - 162.159.193.1 (dải mà TÀI LIỆU Cloudflare ghi cho WireGuard)  -> KHÔNG LÊN
// Không một ngoại lệ nào. Giả lập vị trí lúc đăng ký cũng vô hiệu: hai lần đăng
// ký từ cùng Việt Nam cho hai IP khác nhau (.192.6 và .192.8) => đó chỉ là luân
// phiên trong một dải anycast, không phải chọn theo vị trí.
//
// Muốn chọn PoP thật thì cần IP UNICAST riêng cho từng PoP — đúng thứ Cloudflare
// bán trong Zero Trust ("dedicated egress"), không có ở bản consumer.
//
// TỆ HƠN: bản đầu của file này còn BỊA số ping (RTT thật + hằng số cứng theo nhãn
// node + Random), và PingMonitorService gọi nó trước tiên nên ô PING trên
// Dashboard không liên quan gì tới server game. Xem CLAUDE.md mục
// "Chỉ số hiển thị: KHÔNG được bịa".
//
// GIỮ LẠI vì: cách gọi API đăng ký, cấu trúc GameNode, và phương pháp đo colo
// vẫn hữu ích nếu sau này chuyển sang Zero Trust hoặc relay tự dựng.
// ============================================================
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
    // -1 = không đo được (ICMP bị chặn/timeout). KHÔNG hiển thị số bịa ở đây:
    // trước đây PingNodeAsync trả số tổng hợp nên ô này luôn có giá trị "đẹp".
    public string PingDisplay => PingMs switch
    {
        < 0    => "không đo được",
        >= 999 => "Timeout 🔴",
        < 45   => $"{PingMs} ms 🟢",
        < 80   => $"{PingMs} ms 🟡",
        _      => $"{PingMs} ms 🔴"
    };
}
