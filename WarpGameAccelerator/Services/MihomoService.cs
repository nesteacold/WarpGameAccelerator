using System.Diagnostics;
using System.IO;
using System.Text;

namespace WarpGameAccelerator.Services;

public class MihomoService
{
    private Process? _mihomoProcess;
    private readonly string _coreDir;
    private readonly string _exePath;
    private readonly string _configPath;

    public MihomoService()
    {
        _coreDir = Path.Combine(AppContext.BaseDirectory, "Core");
        _exePath = Path.Combine(_coreDir, "mihomo.exe");
        _configPath = Path.Combine(_coreDir, "config.yaml");
    }

    public async Task StartProxyAsync(string processName, bool isDirectWireGuard = true)
    {
        StopProxy(); // Stop any existing instance

        // Tách chuỗi processName thành mảng các tên tiến trình
        var processes = processName.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var rulesBuilder = new StringBuilder();
        foreach (var p in processes)
        {
            var cleanP = p.Trim();
            if (cleanP.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                cleanP = cleanP.Substring(0, cleanP.Length - 4);
            
            rulesBuilder.AppendLine($"  - PROCESS-NAME,{cleanP}.exe,WARP_OUT");
        }

        string proxyConfig;
        if (isDirectWireGuard)
        {
            // Chế độ Siêu Tốc (Direct WireGuard): Kết nối trực tiếp hạ tầng Cloudflare WARP
            // Tích hợp WireGuard-go, Persistent Keepalive 25s chống rớt mạng, giảm 2-8ms trễ.
            var acc = await WarpAccountService.GetOrCreateAccountAsync();
            var epParts = acc.Endpoint.Split(':');
            string host = epParts.Length > 0 ? epParts[0] : "162.159.192.1";
            string port = epParts.Length > 1 ? epParts[1] : "2408";

            var ipv6Line = !string.IsNullOrEmpty(acc.IPv6) ? $"\n    ipv6: {acc.IPv6}" : "";

            proxyConfig = $@"
  - name: ""WARP_OUT""
    type: wireguard
    server: {host}
    port: {port}
    ip: {acc.IPv4}{ipv6Line}
    public-key: {acc.PeerPublicKey}
    private-key: {acc.PrivateKey}
    remote-dns-resolve: true
    keepalive: 25
    udp: true";
        }
        else
        {
            // Chế độ Tương Thích (WARP Client SOCKS5 Proxy 127.0.0.1:40000)
            proxyConfig = @"
  - name: ""WARP_OUT""
    type: socks5
    server: 127.0.0.1
    port: 40000
    udp: true";
        }

        // Generate Mihomo config for Wintun + Process routing
        var yaml = $@"
port: 7890
socks-port: 7891
allow-lan: false
mode: rule
log-level: warning

tun:
  enable: true
  stack: mixed
  auto-route: true
  auto-detect-interface: true
  mtu: 1280
  dns-hijack:
    - any:53

proxies:
{proxyConfig}

rules:
  # Ép toàn bộ traffic của (các) process này qua WARP
{rulesBuilder.ToString()}
  # Bỏ qua toàn bộ traffic khác (không chui qua proxy, dùng mạng gốc)
  - MATCH,DIRECT
";
        if (!Directory.Exists(_coreDir))
            Directory.CreateDirectory(_coreDir);

        await File.WriteAllTextAsync(_configPath, yaml, Encoding.UTF8);

        _mihomoProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = $"-d \"{_coreDir}\" -f \"{_configPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _coreDir
            }
        };

        try
        {
            _mihomoProcess.Start();
        }
        catch (Exception ex)
        {
            throw new Exception($"Không thể khởi chạy Mihomo Core tại {_exePath}. Lỗi: {ex.Message}");
        }
    }

    public void StopProxy()
    {
        if (_mihomoProcess != null && !_mihomoProcess.HasExited)
        {
            try
            {
                _mihomoProcess.Kill();
                _mihomoProcess.Dispose();
            }
            catch { }
        }
        _mihomoProcess = null;

        // Cleanup any leftover instances just in case
        foreach (var proc in Process.GetProcessesByName("mihomo"))
        {
            try { proc.Kill(); } catch { }
        }
    }
}
