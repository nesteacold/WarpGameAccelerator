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
// Services/CloudflareNodeService.cs
// Quản lý danh sách Node Endpoint Cloudflare, đo Ping UDP/ICMP & Trace PoP
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using WarpGameAccelerator.Models;

namespace WarpGameAccelerator.Services;

public class CloudflareNodeService
{
    private static readonly string NodeConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "selected_node.json");

    // ── Danh sách Node mặc định cho AoW Taiwan & Các Game ──
    public static List<GameNode> GetDefaultNodes()
    {
        return new List<GameNode>
        {
            new GameNode
            {
                Id         = "auto",
                Name       = "Auto (Tự chọn Node tốt nhất)",
                Flag       = "⚡",
                Route      = "Tự động chọn tuyến cáp thông suốt nhất",
                EndpointIp = "162.159.192.1",
                Port       = 2408,
                IsAuto     = true
            },
            new GameNode
            {
                Id         = "vn_hcm_tw01",
                Name       = "VN (HCM) ➔ Taiwan (Taipei 01)",
                Flag       = "🇹🇼",
                Route      = "Cáp nội địa HCM ➔ Cloudflare Backbone ➔ Taiwan",
                EndpointIp = "162.159.192.1",
                Port       = 2408
            },
            new GameNode
            {
                Id         = "vn_hcm_tw02",
                Name       = "VN (HCM) ➔ Taiwan (Taipei 02)",
                Flag       = "🇹🇼",
                Route      = "Cáp nội địa HCM ➔ Cloudflare Clean IP ➔ Taiwan",
                EndpointIp = "162.159.193.1",
                Port       = 2408
            },
            new GameNode
            {
                Id         = "vn_hn_tw01",
                Name       = "VN (Hà Nội) ➔ Taiwan (Taipei 03)",
                Flag       = "🇹🇼",
                Route      = "Cáp nội địa HN ➔ Cloudflare Backbone ➔ Taiwan",
                EndpointIp = "162.159.195.1",
                Port       = 2408
            },
            new GameNode
            {
                Id         = "vn_hkg01",
                Name       = "VN ➔ Hong Kong (HKG 01)",
                Flag       = "🇭🇰",
                Route      = "Tuyến trung chuyển Hong Kong (Dự phòng 1)",
                EndpointIp = "188.114.96.1",
                Port       = 2408
            },
            new GameNode
            {
                Id         = "vn_hkg02",
                Name       = "VN ➔ Hong Kong (HKG 02)",
                Flag       = "🇭🇰",
                Route      = "Tuyến trung chuyển Hong Kong (Dự phòng 2)",
                EndpointIp = "188.114.97.1",
                Port       = 2408
            },
            new GameNode
            {
                Id         = "vn_sin01",
                Name       = "VN ➔ Singapore (SIN 01)",
                Flag       = "🇸🇬",
                Route      = "Tuyến trung chuyển Singapore (Dự phòng 3)",
                EndpointIp = "162.159.196.1",
                Port       = 2408
            }
        };
    }

    // ── Đo Ping End-to-End thực tế tới Game Server qua từng Node ──────────────
    /// <summary>
    /// Đo RTT ICMP THẬT tới endpoint của node. Trả về -1 nếu không đo được.
    ///
    /// KHÔNG cộng thêm hằng số theo nhãn node, KHÔNG jitter ngẫu nhiên, KHÔNG
    /// bịa số khi lỗi. Bản trước làm cả ba: lấy RTT thật rồi cộng offset cứng
    /// theo tên node (tw01 +32, tw02 +35, hkg01 +42...) cộng Random 0-2ms, và
    /// nhánh catch trả về 36/47/58 + Random. Hệ quả: thứ tự "Taiwan tốt hơn HK
    /// tốt hơn SIN" là do hằng số viết cứng, không phải kết quả đo — trong khi
    /// đo thật thì cả 4 endpoint đều ~48-50ms vì đều là anycast Cloudflare và
    /// cùng về một PoP (đo được: colo=SIN cho mọi endpoint từ ISP Việt Nam).
    ///
    /// LƯU Ý VỀ Ý NGHĨA: đây là RTT tới EDGE Cloudflare, KHÔNG phải ping tới
    /// server game. Đừng dùng giá trị này làm "ping game" (xem PingMonitorService).
    /// </summary>
    public static async Task<int> PingNodeAsync(GameNode node)
    {
        if (string.IsNullOrWhiteSpace(node.EndpointIp)) return -1;

        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(node.EndpointIp, 1200);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                 ? (int)reply.RoundtripTime
                 : -1;
        }
        catch
        {
            return -1;
        }
    }

    // ── Đo Ping song song toàn bộ các Node trong ~300ms ────────
    public static async Task PingAllNodesAsync(List<GameNode> nodes)
    {
        var tasks = nodes.Where(n => !n.IsAuto).Select(async node =>
        {
            node.PingMs = await PingNodeAsync(node);
        });

        await Task.WhenAll(tasks);

        // Gán Auto = Node có Ping thấp nhất
        var autoNode = nodes.FirstOrDefault(n => n.IsAuto);
        if (autoNode != null)
        {
            // Chỉ xét node ĐO ĐƯỢC (PingMs >= 0). Trước đây Min() lấy cả -1 nên
            // node lỗi lại thành "tốt nhất".
            var measured = nodes.Where(x => !x.IsAuto && x.PingMs >= 0).ToList();
            autoNode.PingMs = measured.Count > 0 ? measured.Min(x => x.PingMs) : -1;
        }
    }

    // ── Lưu / Nạp Node đã chọn ─────────────────────────────
    public static async Task SaveSelectedNodeAsync(GameNode node)
    {
        try
        {
            var dir = Path.GetDirectoryName(NodeConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(NodeConfigPath, json);
        }
        catch { }
    }

    public static GameNode GetSelectedNode()
    {
        try
        {
            if (File.Exists(NodeConfigPath))
            {
                var json = File.ReadAllText(NodeConfigPath);
                var node = JsonSerializer.Deserialize<GameNode>(json);
                if (node != null) return node;
            }
        }
        catch { }

        // Mặc định trả về Auto
        return GetDefaultNodes()[0];
    }
}
