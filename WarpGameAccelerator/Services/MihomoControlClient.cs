using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Client REST mỏng gọi mihomo external-controller (v1.19.29) — dùng để chuyển
/// candidate đang active trong 1 proxy-group "type: select" mà KHÔNG rebuild/restart
/// process mihomo. Đây là cơ chế cốt lõi của tính năng multi-candidate MASQUE (xem
/// MihomoService's DirectMasqueBeta useCandidateGroups branch): rebuild toàn bộ
/// process = dựng tunnel mới = "quay số" lại colo, còn PUT /proxies/{group} chỉ đổi
/// 1 field nội bộ (adapter/outboundgroup/selector.go Selector.Set) — không rebuild.
///
/// Mọi lỗi (mất kết nối tới external-controller, JSON hỏng, HTTP lỗi...) nuốt vào
/// false/null — hàm này TUYỆT ĐỐI không được throw vào luồng Start/Stop Boost hay
/// vào MasqueTunnelSweepService.
/// </summary>
public sealed class MihomoControlClient
{
    private readonly HttpClient _http;

    public MihomoControlClient(int controllerPort, string secret)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{controllerPort}/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    /// <summary>PUT /proxies/{group} {"name": proxyName} — chuyển thành viên đang active của group.</summary>
    public async Task<bool> SelectAsync(string group, string proxyName)
    {
        try
        {
            using var body = JsonContent.Create(new { name = proxyName });
            using var resp = await _http.PutAsync($"proxies/{Uri.EscapeDataString(group)}", body);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>GET /proxies/{group} → đọc field "now" (tên thành viên đang active).</summary>
    public async Task<string?> GetSelectedAsync(string group)
    {
        try
        {
            using var resp = await _http.GetAsync($"proxies/{Uri.EscapeDataString(group)}");
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("now", out var now))
                return now.GetString();
        }
        catch
        {
            // Nuốt lỗi — xem ghi chú ở đầu class.
        }
        return null;
    }
}

/// <summary>
/// Trạng thái phiên multi-candidate MASQUE đang chạy trong core mihomo hiện tại —
/// giữ port + secret của external-controller CHỈ TRONG RAM (không persist, không
/// log) cùng danh sách candidate (tên outbound → endpoint) đã đưa vào 2 proxy-group
/// GAME-PATH/PROBE-PATH lúc sinh config.yaml. Xem MihomoService.ActiveMasqueControl.
/// </summary>
public sealed record MasqueControlSession(
    int ControllerPort,
    string Secret,
    MihomoControlClient Client,
    IReadOnlyDictionary<string, MasqueEndpoint> Candidates);
