using System.Diagnostics;
using System.IO;
using System.Text;

namespace WarpGameAccelerator.Services;

public class MihomoService
{
    private Process? _mihomoProcess;
    private CancellationTokenSource? _activeStartCts;
    private readonly string _coreDir;
    private readonly string _exePath;
    private readonly string _configPath;

    public MihomoService()
    {
        // Khi bundle chung 1 file, không được lưu Core vào AppContext vì quyền/read-only
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _coreDir = Path.Combine(appData, "WarpGameAccelerator", "Core");
        _exePath = Path.Combine(_coreDir, "mihomo.exe");
        _configPath = Path.Combine(_coreDir, "config.yaml");

        ExtractCoreResources();
    }

    private void ExtractCoreResources()
    {
        StopProxy();

        if (!Directory.Exists(_coreDir))
            Directory.CreateDirectory(_coreDir);

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var currentVersion = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var versionFilePath = Path.Combine(_coreDir, ".extracted_version");

        // Bỏ qua re-extract nếu version không đổi và mihomo.exe đã tồn tại —
        // tránh ghi lại ~50MB ra đĩa mỗi lần app khởi động.
        if (File.Exists(versionFilePath) && File.Exists(_exePath))
        {
            string savedVersion = "";
            try { savedVersion = File.ReadAllText(versionFilePath).Trim(); } catch { }
            if (savedVersion == currentVersion) return;
        }

        // EmbeddedResource namespace pattern: ProjectName.FolderName.FileName
        var resourcesToExtract = new[] {
            "mihomo.exe",
            "geoip.metadat",
            "geosite.dat",
            "Country.mmdb"
        };

        var allResourceNames = assembly.GetManifestResourceNames();

        foreach (var file in resourcesToExtract)
        {
            var resName = allResourceNames.FirstOrDefault(r => r.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                var destPath = Path.Combine(_coreDir, file);
                using var stream = assembly.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var fileStream = File.Create(destPath);
                    stream.CopyTo(fileStream);
                }
            }
        }

        try
        {
            File.WriteAllText(versionFilePath, currentVersion);
        }
        catch { }
    }

    public async Task StartProxyAsync(string processName, bool isDirectWireGuard = true)
    {
        // Hủy mọi lệnh Start đang dở trước khi bắt đầu lệnh mới — tránh 2 lệnh
        // Start chồng chéo ghi đè config lẫn nhau, hoặc lệnh cũ "hồi sinh" tunnel
        // sau khi StopProxy() đã được gọi (disconnect) trong lúc nó đang chờ.
        _activeStartCts?.Cancel();
        var cts = new CancellationTokenSource();
        _activeStartCts = cts;
        var token = cts.Token;

        KillMihomoProcess(); // Stop any existing instance (không hủy token của chính lệnh này)
        try
        {
            await Task.Delay(1000, token); // Chờ 1 giây để Windows Kernel giải phóng card mạng Wintun Meta và Socket Bindings
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string proxyName = isDirectWireGuard ? "WARP-Direct" : "WARP_OUT";

        // Tách chuỗi processName thành mảng các tên tiến trình
        var processes = processName.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var rulesBuilder = new StringBuilder();
        foreach (var p in processes)
        {
            var cleanP = p.Trim();
            if (cleanP.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                cleanP = cleanP.Substring(0, cleanP.Length - 4);
            
            rulesBuilder.AppendLine($"  - PROCESS-NAME,{cleanP}.exe,{proxyName}");
        }

        string proxyConfig;
        string excludeRoute = "";
        
        if (isDirectWireGuard)
        {
            // Chế độ Siêu Tốc (Direct Mode Cloudflare WARP WireGuard)
            var acc = await WarpAccountService.GetOrCreateAccountAsync();
            var selectedNode = CloudflareNodeService.GetSelectedNode();
            string host = "162.159.192.1";
            int port = 2408;

            if (selectedNode != null && !selectedNode.IsAuto && !string.IsNullOrEmpty(selectedNode.EndpointIp))
            {
                host = selectedNode.EndpointIp;
                port = selectedNode.Port;
            }
            else if (!string.IsNullOrEmpty(acc.Endpoint))
            {
                // Dùng endpoint thật từ tài khoản (wgcf trả về host dạng
                // "engage.cloudflareclient.com:2408" hoặc "IP:port")
                var lastColon = acc.Endpoint.LastIndexOf(':');
                if (lastColon > 0)
                {
                    host = acc.Endpoint[..lastColon];
                    if (int.TryParse(acc.Endpoint[(lastColon + 1)..], out var parsedPort) && parsedPort > 0)
                        port = parsedPort;
                }
            }
            acc.Endpoint = $"{host}:{port}";

            byte[] clientBytes = new byte[3];
            if (!string.IsNullOrEmpty(acc.ClientId))
            {
                try
                {
                    var b = Convert.FromBase64String(acc.ClientId);
                    if (b.Length >= 3)
                    {
                        clientBytes[0] = b[0];
                        clientBytes[1] = b[1];
                        clientBytes[2] = b[2];
                    }
                }
                catch { }
            }

            proxyConfig = $@"
  - name: {proxyName}
    type: wireguard
    server: {host}
    port: {port}
    ip: {acc.IPv4}
    public-key: {acc.PeerPublicKey}
    private-key: {acc.PrivateKey}
    reserved: [{clientBytes[0]}, {clientBytes[1]}, {clientBytes[2]}]
    mtu: 1280
    udp: true
    remote-dns-resolve: true
    keepalive: 25";
            
            // inet4-route-exclude-address chỉ nhận IP thuần, không nhận hostname
            // (server có thể là "engage.cloudflareclient.com" khi lấy từ acc.Endpoint)
            if (System.Net.IPAddress.TryParse(host, out _))
            {
                excludeRoute = $"\n  inet4-route-exclude-address:\n    - {host}/32";
            }
        }
        else
        {
            // Chế độ Tương Thích (WARP Client SOCKS5 Proxy 127.0.0.1:40000)
            proxyConfig = @"
  - name: ""WARP_OUT""
    type: socks5
    server: 127.0.0.1
    port: 40000
    udp: true
    skip-cert-verify: true";
        }

        string dnsAndTunConfig = $@"
dns:
  enable: true
  listen: 0.0.0.0:1053
  ipv6: false
  enhanced-mode: redir-host
  default-nameserver:
    - 1.1.1.1
    - 1.0.0.1
  nameserver:
    - 1.1.1.1
    - 1.0.0.1
tun:
  enable: true
  stack: mixed
  auto-route: true
  auto-detect-interface: true{excludeRoute}
  mtu: 1280
  tcp-concurrent: true
  dns-hijack:
    - any:53";

        // Generate Mihomo config
        var yaml = $@"
port: 7890
socks-port: 7891
allow-lan: false
mode: rule
log-level: warning
{dnsAndTunConfig}

proxies:
{proxyConfig}

rules:
  - IP-CIDR,127.0.0.0/8,DIRECT
  - IP-CIDR,192.168.0.0/16,DIRECT
  - IP-CIDR,10.0.0.0/8,DIRECT
  - IP-CIDR,172.16.0.0/12,DIRECT
  - IP-CIDR,1.1.1.1/32,{proxyName}
  - IP-CIDR,1.0.0.1/32,{proxyName}
  # Ép toàn bộ traffic của (các) process này qua WARP
{rulesBuilder.ToString()}
  # Giữ nguyên toàn bộ ứng dụng khác chạy trên mạng nhà (Split Tunneling chuẩn)
  - MATCH,DIRECT
";
        if (token.IsCancellationRequested) return;

        if (!Directory.Exists(_coreDir))
            Directory.CreateDirectory(_coreDir);

        await File.WriteAllTextAsync(_configPath, yaml, Encoding.UTF8);

        if (token.IsCancellationRequested) return;

        var runtimeLogPath = Path.Combine(_coreDir, "mihomo_runtime.log");
        try { File.WriteAllText(runtimeLogPath, string.Empty); } catch { }

        _mihomoProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = $"-d \"{_coreDir}\" -f \"{_configPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _coreDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        _mihomoProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            try { File.AppendAllText(runtimeLogPath, e.Data + Environment.NewLine); } catch { }
        };
        _mihomoProcess.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            try { File.AppendAllText(runtimeLogPath, e.Data + Environment.NewLine); } catch { }
        };

        try
        {
            _mihomoProcess.Start();
            _mihomoProcess.BeginOutputReadLine();
            _mihomoProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            throw new Exception($"Không thể khởi chạy Mihomo Core tại {_exePath}. Lỗi: {ex.Message}");
        }

        if (token.IsCancellationRequested)
        {
            // Bị hủy ngay trước khi kịp start xong (ví dụ user vừa Disconnect) —
            // dừng lại ngay, không để tunnel treo lại sau khi đã bị yêu cầu ngắt.
            KillMihomoProcess();
        }
    }

    public void StopProxy()
    {
        _activeStartCts?.Cancel();
        KillMihomoProcess();
    }


    private void KillMihomoProcess()
    {
        if (_mihomoProcess != null && !_mihomoProcess.HasExited)
        {
            try
            {
                _mihomoProcess.Kill();
                _mihomoProcess.WaitForExit(1000);
                _mihomoProcess.Dispose();
            }
            catch { }
        }
        _mihomoProcess = null;

        // Cleanup any leftover instances just in case
        foreach (var proc in Process.GetProcessesByName("mihomo"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(1000);
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }
}
