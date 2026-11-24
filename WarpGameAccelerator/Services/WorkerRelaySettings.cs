using System.Net;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Cấu hình tùy chọn cho tuyến dự phòng Bước 4: Cloudflare Worker TCP-over-WebSocket
/// (VLESS/WS) đi qua Preferred Edge IP (mặc định 108.162.198.1:443 — đo được 29–31 ms
/// thẳng tới colo HKG trên IPv4, hữu ích đặc biệt cho máy KHÔNG có IPv6).
///
/// File lưu tại %LocalAppData%\WarpGameAccelerator\Data\worker_relay.json.
/// Mặc định TẮT (không có file hoặc enabled=false) → không ảnh hưởng người dùng chưa
/// deploy Worker. Không bao giờ ghi UUID vào log; hỗ trợ đọc/ghi qua SecretStore (DPAPI)
/// hoặc đọc plaintext nếu người dùng tự sửa file JSON tay.
/// </summary>
public sealed record WorkerRelayConfig(
    bool Enabled,
    string WorkerHost,
    string Uuid,
    string PreferredIp,
    int Port,
    string Path);

public static class WorkerRelaySettings
{
    public const string DefaultPreferredIp = "108.162.198.1";
    public const int DefaultPort = 443;
    public const string DefaultPath = "/vless";
    public const string ProxyName = "CF-Worker-HKG";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "worker_relay.json");

    public static WorkerRelayConfig? LoadActive(string? overridePath = null)
    {
        try
        {
            string path = overridePath ?? FilePath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (!root.TryGetProperty("enabled", out var enEl) || !enEl.GetBoolean())
                return null;

            string host = root.TryGetProperty("workerHost", out var hEl) ? (hEl.GetString() ?? "").Trim() : "";
            string rawUuid = root.TryGetProperty("uuid", out var uEl) ? (uEl.GetString() ?? "").Trim() : "";
            string uuid = SecretStore.Unprotect(rawUuid).Trim();
            string prefIp = root.TryGetProperty("preferredIp", out var ipEl) && !string.IsNullOrWhiteSpace(ipEl.GetString())
                ? ipEl.GetString()!.Trim()
                : DefaultPreferredIp;
            int port = root.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var pv) && pv is > 0 and <= 65535
                ? pv
                : DefaultPort;
            string wsPath = root.TryGetProperty("path", out var wsEl) && !string.IsNullOrWhiteSpace(wsEl.GetString())
                ? wsEl.GetString()!.Trim()
                : DefaultPath;
            if (!wsPath.StartsWith('/')) wsPath = "/" + wsPath;

            if (!IsValidHost(host) || !Guid.TryParse(uuid, out _))
                return null;
            if (!IPAddress.TryParse(prefIp, out _))
                return null;

            return new WorkerRelayConfig(true, host, uuid, prefIp, port, wsPath);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WorkerRelayConfig cfg, string? overridePath = null)
    {
        string path = overridePath ?? FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var payload = new
        {
            enabled = cfg.Enabled,
            workerHost = cfg.WorkerHost.Trim(),
            uuid = SecretStore.Protect(cfg.Uuid.Trim()),
            preferredIp = string.IsNullOrWhiteSpace(cfg.PreferredIp) ? DefaultPreferredIp : cfg.PreferredIp.Trim(),
            port = cfg.Port is > 0 and <= 65535 ? cfg.Port : DefaultPort,
            path = string.IsNullOrWhiteSpace(cfg.Path) ? DefaultPath : cfg.Path.Trim()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Sinh khối YAML outbound Mihomo chuẩn VLESS-over-WebSocket TLS.</summary>
    public static string BuildMihomoOutboundYaml(WorkerRelayConfig cfg)
    {
        return $@"
  - name: {ProxyName}
    type: vless
    server: ""{cfg.PreferredIp}""
    port: {cfg.Port}
    uuid: ""{cfg.Uuid}""
    tls: true
    servername: ""{cfg.WorkerHost}""
    network: ws
    ws-opts:
      path: ""{cfg.Path}""
      headers:
        Host: ""{cfg.WorkerHost}""
    udp: false";
    }

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        if (host.Contains('\n') || host.Contains('\r') || host.Contains('"') || host.Contains(' ')) return false;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}
