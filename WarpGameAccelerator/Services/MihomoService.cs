using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WarpGameAccelerator.Models;

namespace WarpGameAccelerator.Services;

public class MihomoService
{
    // IP anycast Cloudflare cho consumer-masque.cloudflareclient.com — dùng khi resolver
    // hệ thống không trả bản ghi A (fallback, không phải giá trị chính).
    private const string MasqueFallbackIp = "162.159.198.2";

    private GracefulProcessLauncher.LaunchedProcess? _launched;
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

    public Task StartProxyAsync(string processName, bool isDirectWireGuard) =>
        StartProxyAsync(processName, isDirectWireGuard ? EngineMode.DirectWireGuard : EngineMode.WarpClientProxy);

    /// <summary>Kênh game (WARP/MASQUE) đang được yêu cầu chạy — độc lập với kênh cá nhân.</summary>
    public bool IsGameChannelActive { get; private set; }

    private string _gameProcessName = "";
    private EngineMode _gameEngineMode = EngineMode.DirectWireGuard;

    public async Task StartProxyAsync(string processName, EngineMode mode = EngineMode.DirectWireGuard)
    {
        _gameProcessName = processName;
        _gameEngineMode = mode;
        IsGameChannelActive = true;
        await ApplyChannelsAsync();
    }

    /// <summary>
    /// Kênh cá nhân (Dev Panel) bật/tắt độc lập với kênh game — có thể bật
    /// mà không cần Boost game trước. Vì mihomo không hỗ trợ hot-reload
    /// (Phase 2 đã khảo sát: mọi thay đổi config đều cần kill+restart toàn bộ
    /// process), bật/tắt kênh này khi kênh game đang chạy sẽ gây restart
    /// mihomo — kênh game gián đoạn ngắn (~1-3s) rồi tự phục hồi, không bị
    /// tắt hẳn. Đây là trade-off cố ý để giữ đúng 1 TUN adapter duy nhất.
    /// </summary>
    public async Task SetPersonalChannelActiveAsync(bool active)
    {
        PersonalVpnService.SetActive(active);
        await ApplyChannelsAsync();
    }

    /// <summary>
    /// Gọi khi người dùng đổi profile Active hoặc xoá profile đang dùng
    /// trong lúc kênh cá nhân đang chạy — bắt buộc rebuild+restart để mihomo
    /// không tiếp tục route theo config cũ đã nằm trong bộ nhớ tiến trình.
    /// </summary>
    public async Task ApplyPersonalProfileChangeAsync()
    {
        if (PersonalVpnService.IsChannelActive())
            await ApplyChannelsAsync();
    }

    /// <summary>
    /// Rebuild config.yaml theo trạng thái hiện tại của CẢ 2 kênh
    /// (<see cref="IsGameChannelActive"/> + <see cref="PersonalVpnService.IsChannelActive"/>)
    /// rồi kill + restart mihomo. Nếu cả 2 đều tắt, dừng hẳn mihomo (không
    /// restart lại rỗng). Đây là điểm duy nhất thực sự start/stop process —
    /// StartProxyAsync/StopProxy/SetPersonalChannelActiveAsync chỉ cập nhật
    /// state rồi gọi vào đây.
    /// </summary>
    private async Task ApplyChannelsAsync()
    {
        bool personalActive = PersonalVpnService.IsChannelActive();

        if (!IsGameChannelActive && !personalActive)
        {
            _activeStartCts?.Cancel();
            TryGracefulStop();
            KillMihomoProcess();
            return;
        }

        // Hủy mọi lệnh Start đang dở trước khi bắt đầu lệnh mới — tránh 2 lệnh
        // Start chồng chéo ghi đè config lẫn nhau, hoặc lệnh cũ "hồi sinh" tunnel
        // sau khi StopProxy() đã được gọi (disconnect) trong lúc nó đang chờ.
        _activeStartCts?.Cancel();
        var cts = new CancellationTokenSource();
        _activeStartCts = cts;
        var token = cts.Token;

        KillMihomoProcess(); // Stop any existing instance (không hủy token của chính lệnh này)

        // Chờ instance cũ thực sự nhả cổng (7890/7891) rồi mới khởi động lại.
        // Trước đây chờ mù 1 giây: vừa chậm khi kernel nhả nhanh, vừa không đủ
        // khi nhả chậm → instance mới bind cổng thất bại.
        if (!await WaitForPortsReleasedAsync(token)) return;

        string processName = _gameProcessName;
        EngineMode mode = _gameEngineMode;

        string proxyName = mode switch
        {
            EngineMode.DirectWireGuard   => "WARP-Direct",
            EngineMode.DirectMasqueBeta  => "WARP-Masque",
            _                            => "WARP_OUT"
        };

        string proxyConfig = "";
        string excludeRoute = "";
        var rulesBuilder = new StringBuilder();
        // Rule ưu tiên tới các endpoint đăng ký/handshake Cloudflare — chỉ cần
        // khi kênh game đang chạy, không liên quan gì tới kênh cá nhân.
        string cloudflareApiRules = "";

        if (!IsGameChannelActive)
        {
            // Chỉ kênh cá nhân đang chạy — bỏ hẳn section proxy/rule game,
            // mihomo chỉ phục vụ Personal-WG (+ MATCH,DIRECT cho traffic còn lại).
        }
        else
        {
        cloudflareApiRules = $@"  - IP-CIDR,1.1.1.1/32,{proxyName}
  - IP-CIDR,1.0.0.1/32,{proxyName}
";

        // Tách chuỗi processName thành mảng các tên tiến trình
        var processes = processName.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in processes)
        {
            var cleanP = p.Trim();
            if (cleanP.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                cleanP = cleanP.Substring(0, cleanP.Length - 4);

            rulesBuilder.AppendLine($"  - PROCESS-NAME,{cleanP}.exe,{proxyName}");
        }

        if (mode == EngineMode.DirectWireGuard)
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
            byte[] clientBytes = ExtractClientBytes(acc.ClientId);

            // Cổng 2408 (mặc định WireGuard/WARP) bị router/ISP bóp riêng
            // chiều upload trên máy đã test (đo được ~1 Mbps cố định, lặp lại
            // nhiều lần). Đổi sang 4500 — đã kiểm chứng đạt băng thông tốt hơn
            // rõ rệt trên cùng mạng này.
            port = 4500;

            acc.Endpoint = $"{host}:{port}";

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
        else if (mode == EngineMode.DirectMasqueBeta)
        {
            // BETA — Direct Mode qua MASQUE (QUIC/HTTP-3). Tài khoản/key HOÀN
            // TOÀN riêng (WarpMasqueAccountInfo) — không đụng, không ảnh hưởng
            // gì tới tài khoản/config của Direct WireGuard ở nhánh trên.
            var masqueAcc = await WarpAccountService.GetOrCreateMasqueAccountAsync();

            // mihomo masque adapter tự resolve field "server" bằng resolver riêng của nó
            // (không đi qua fake-ip DNS của app) — dùng hostname trực tiếp bị "dns resolve
            // failed: couldn't find ip" cho MỌI traffic. Toàn bộ nhánh WireGuard ở trên luôn
            // dùng IP thô cho "server" (không phải hostname) — áp dụng lại quy tắc đó ở đây:
            // resolve hostname ra IP thật, giữ hostname gốc ở field "sni" để TLS ClientHello
            // vẫn đúng.
            string masqueServerIp = masqueAcc.Server;
            if (!System.Net.IPAddress.TryParse(masqueAcc.Server, out _))
            {
                try
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(masqueAcc.Server);
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (v4 != null) masqueServerIp = v4.ToString();
                    else masqueServerIp = MasqueFallbackIp;
                }
                catch
                {
                    // Resolver hệ thống lỗi/không có bản ghi A cho hostname MASQUE consumer —
                    // fallback về IP anycast Cloudflare cố định (giữ hostname gốc ở "sni").
                    masqueServerIp = MasqueFallbackIp;
                }
            }

            proxyConfig = $@"
  - name: {proxyName}
    type: masque
    server: {masqueServerIp}
    sni: {masqueAcc.Server}
    port: {masqueAcc.Port}
    ip: {masqueAcc.IPv4}
    private-key: {masqueAcc.PrivateKey}
    public-key: {masqueAcc.PeerPublicKey}
    mtu: 1280
    udp: true
    remote-dns-resolve: true";

            if (System.Net.IPAddress.TryParse(masqueServerIp, out _))
            {
                excludeRoute = $"\n  inet4-route-exclude-address:\n    - {masqueServerIp}/32";
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
        } // end if (IsGameChannelActive)

        // Kênh VPN cá nhân (Dev Panel) — outbound RIÊNG, additive, hoàn toàn
        // độc lập với mode/proxyName ở trên. Chạy song song bất kể game đang
        // dùng WireGuard hay MASQUE, không đụng cấu trúc proxy chính.
        string personalProxyConfig = "";
        string personalRules = "";
        if (PersonalVpnService.TryGetActiveValidConfig(out var personalCfg) && personalCfg != null)
        {
            var lastColon = personalCfg.Endpoint.LastIndexOf(':');
            string pHost = lastColon > 0 ? personalCfg.Endpoint[..lastColon] : personalCfg.Endpoint;
            string pPort = lastColon > 0 ? personalCfg.Endpoint[(lastColon + 1)..] : "51820";

            // mihomo tự resolve DNS cho field "server" bằng resolver riêng của nó
            // (không qua DNS hệ thống) — hostname bị fail âm thầm, dial luôn
            // "context deadline exceeded" dù server thật hoạt động tốt (đúng bug
            // đã gặp với MASQUE — xem comment resolve masqueServerIp ở trên).
            // WireGuard không có TLS/SNI nên không cần giữ lại hostname gốc.
            if (!System.Net.IPAddress.TryParse(pHost, out _))
            {
                try
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(pHost);
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (v4 != null) pHost = v4.ToString();
                }
                catch
                {
                    // Resolve lỗi thì giữ nguyên hostname — vẫn có khả năng mihomo
                    // tự resolve được tuỳ trường hợp, hơn là chặn hẳn không chạy.
                }
            }

            personalProxyConfig = $@"
  - name: Personal-WG
    type: wireguard
    server: {pHost}
    port: {pPort}
    ip: {personalCfg.AddressV4}
    private-key: {personalCfg.PrivateKey}
    public-key: {personalCfg.PeerPublicKey}
    udp: true";

            if (!string.IsNullOrEmpty(personalCfg.PresharedKey))
                // Field ĐÚNG trong mihomo là "pre-shared-key" (có gạch nối) — viết sai
                // thành "preshared-key" bị mihomo lặng lẽ bỏ qua (không lỗi parse),
                // nhưng server có áp PSK cho peer này thì handshake luôn fail (crypto
                // không khớp) — đúng triệu chứng "context deadline exceeded" 100% mọi đích.
                personalProxyConfig += $"\n    pre-shared-key: {personalCfg.PresharedKey}";

            var personalRulesBuilder = new StringBuilder();
            foreach (var p in personalCfg.ProcessNames)
            {
                var cleanP = p.Trim();
                if (cleanP.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    cleanP = cleanP[..^4];
                personalRulesBuilder.AppendLine($"  - PROCESS-NAME,{cleanP}.exe,Personal-WG");
            }
            personalRules = "  # Kênh VPN cá nhân (Dev Panel) — độc lập engine game\n" + personalRulesBuilder.ToString();
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
# BẮT BUỘC 'always' — mặc định 'strict' khiến mihomo bỏ qua việc tra cứu tiến
# trình với một số kết nối đi qua TUN. Khi đó luật PROCESS-NAME không khớp và
# kết nối tụt xuống MATCH,DIRECT, đi thẳng ra ISP rồi timeout (~20s) vì server
# game chỉ vào được qua tunnel — biểu hiện là đăng nhập rất chậm.
# Bằng chứng: log ghi 'dial DIRECT (match Match/) ... --> dd.woniu.com:80'
# KHÔNG kèm tên tiến trình trong ngoặc, tức là không nhận diện được.
# Đây là cách sửa đúng gốc, giữ nguyên Split Tunneling theo tiến trình —
# KHÔNG dùng luật DOMAIN-SUFFIX để vá, vì như vậy mọi ứng dụng (kể cả trình
# duyệt) truy cập domain đó đều bị kéo qua tunnel, ăn vào băng thông WARP+.
find-process-mode: always
{dnsAndTunConfig}

proxies:
{proxyConfig}{personalProxyConfig}

rules:
{cloudflareApiRules}  # PROCESS-NAME phải đứng TRƯỚC các rule DIRECT theo dải IP riêng bên dưới —
  # mihomo match rule theo thứ tự, dòng nào khớp trước thắng. Kênh VPN cá
  # nhân cố ý route tới LAN riêng (192.168.x.x...) của server đích XUYÊN
  # QUA tunnel — nếu để rule DIRECT theo dải IP riêng lên trước, mọi traffic
  # tới đích dạng 192.168.x.x/10.x.x.x bị chặn về DIRECT ngay, không bao giờ
  # tới được rule Personal-WG/WARP dù đúng process đã chọn.
  # Ép toàn bộ traffic của (các) process này qua WARP
{rulesBuilder.ToString()}
{personalRules}  # Chỉ áp DIRECT cho dải IP riêng với traffic KHÔNG thuộc 2 nhóm process trên
  - IP-CIDR,127.0.0.0/8,DIRECT
  - IP-CIDR,192.168.0.0/16,DIRECT
  - IP-CIDR,10.0.0.0/8,DIRECT
  - IP-CIDR,172.16.0.0/12,DIRECT
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

        try
        {
            _launched = GracefulProcessLauncher.Start(
                _exePath, $"-d \"{_coreDir}\" -f \"{_configPath}\"", _coreDir);
            PumpLogAsync(_launched.StdOutRead, runtimeLogPath);
            PumpLogAsync(_launched.StdErrRead, runtimeLogPath);
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

    private static byte[] ExtractClientBytes(string clientId)
    {
        var clientBytes = new byte[3];
        if (!string.IsNullOrEmpty(clientId))
        {
            try
            {
                var b = Convert.FromBase64String(clientId);
                if (b.Length >= 3)
                {
                    clientBytes[0] = b[0];
                    clientBytes[1] = b[1];
                    clientBytes[2] = b[2];
                }
            }
            catch { }
        }
        return clientBytes;
    }

    public void StopProxy()
    {
        IsGameChannelActive = false;

        if (PersonalVpnService.IsChannelActive())
        {
            // Kênh cá nhân vẫn cần chạy — rebuild+restart chỉ với section cá
            // nhân thay vì kill hẳn. Fire-and-forget: StopProxy() vẫn giữ chữ
            // ký void/sync như code gọi hiện có (DashboardViewModel), việc
            // restart async chạy ngầm không chặn UI.
            _ = ApplyChannelsAsync();
            return;
        }

        _activeStartCts?.Cancel();
        TryGracefulStop();
        KillMihomoProcess();
    }

    /// <summary>
    /// Gửi CTRL_BREAK_EVENT để mihomo tự chạy cleanup (gỡ route, khôi phục DNS,
    /// đóng TUN adapter) trước khi thoát. Process.Kill() ngay không cho nó cơ
    /// hội này → để lại route/DNS bẩn trỏ vào adapter đã mất, gây mất internet
    /// sau khi Stop Boost. KillMihomoProcess() vẫn được gọi ngay sau đây làm
    /// lưới an toàn (fallback) nếu mihomo không thoát kịp.
    /// </summary>
    private void TryGracefulStop()
    {
        var launched = _launched;
        if (launched == null) return;

        try
        {
            Process? proc = null;
            try { proc = Process.GetProcessById(launched.ProcessId); } catch { }

            if (proc != null && !proc.HasExited && launched.TrySendCtrlBreak())
                proc.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[MihomoService] Graceful stop lỗi: {ex.Message}");
        }
        finally
        {
            launched.Dispose();
            _launched = null;
        }
    }

    private static async void PumpLogAsync(SafeFileHandle readHandle, string logPath)
    {
        try
        {
            using var stream = new FileStream(readHandle, FileAccess.Read);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Chờ tới khi cổng HTTP/SOCKS của mihomo được nhả hẳn (instance cũ đã
    /// giải phóng socket + card mạng Wintun). Trả về false nếu bị hủy giữa
    /// chừng; hết timeout thì vẫn trả true để không chặn người dùng vô hạn.
    /// </summary>
    private static async Task<bool> WaitForPortsReleasedAsync(CancellationToken token)
    {
        const int timeoutMs   = 5000;
        const int pollInterval = 100;

        for (int waited = 0; waited < timeoutMs; waited += pollInterval)
        {
            if (token.IsCancellationRequested) return false;

            if (ArePortsFree())
            {
                if (waited > 0)
                    DiagnosticLogService.Trace($"Mihomo: cổng được nhả sau {waited}ms");
                return true;
            }

            try
            {
                await Task.Delay(pollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        DiagnosticLogService.Trace("Mihomo: hết 5s chờ nhả cổng, vẫn khởi động tiếp");
        return true;
    }

    private static bool ArePortsFree()
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();

            foreach (var ep in listeners)
            {
                if (ep.Port == 7890 || ep.Port == 7891) return false;
            }
            return true;
        }
        catch
        {
            return true; // Không kiểm tra được thì đừng chặn việc khởi động
        }
    }


    private void KillMihomoProcess()
    {
        if (_launched != null)
        {
            try
            {
                var proc = Process.GetProcessById(_launched.ProcessId);
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
            }
            catch { }
            finally
            {
                _launched.Dispose();
                _launched = null;
            }
        }

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
