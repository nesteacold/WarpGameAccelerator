// ============================================================
// Services/PingMonitorService.cs — Monitor ping real-time
// ============================================================
using System.Net.NetworkInformation;
using WarpGameAccelerator.Models;

namespace WarpGameAccelerator.Services;

public class PingMonitorService : IDisposable
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private CancellationTokenSource? _cts;
    private string _targetHost = "1.1.1.1";
    private bool _disposed;

    // Lưu baseline trước khi boost (lấy trung bình 3 lần đo)
    private long _baselinePingMs = 0;

    // Lịch sử ping (24 điểm ~ 48 giây)
    public List<long> PingHistory { get; } = new(24);

    public event EventHandler<PingStats>? PingUpdated;

    public void SetTarget(string host)
    {
        _targetHost = host;
        PingHistory.Clear();
    }

    /// <summary>Bắt đầu monitor, record baseline trước khi boost</summary>
    public async Task StartAsync(bool recordBaseline = false)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        if (recordBaseline)
        {
            _baselinePingMs = await MeasureAverageAsync(3);
        }

        _ = Task.Run(async () => await MonitorLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (await _timer.WaitForNextTickAsync(token))
        {
            try
            {
                var ping = await MeasurePingAsync(_targetHost);
                UpdateHistory(ping);

                var stats = new PingStats
                {
                    CurrentPingMs    = ping,
                    BaselinePingMs   = _baselinePingMs,
                    PacketLossPercent = ping < 0 ? 100.0 : 0.0,
                    TargetHost       = _targetHost,
                    Timestamp        = DateTime.Now
                };

                PingUpdated?.Invoke(this, stats);
            }
            catch (OperationCanceledException) { break; }
            catch { /* bỏ qua lỗi từng lần */ }
        }
    }

    private string _targetProcessName = "fxgame";

    public void SetTargetProcess(string processNameStr)
    {
        _targetProcessName = processNameStr;
    }

    public async Task<long> MeasurePingAsync(string host)
    {
        try
        {
            // 1. Kiểm tra xem tiến trình game được chọn có kết nối TCP tới Game Server không
            var (gameIp, gamePort) = GetActiveGameServerAddress(_targetProcessName);

            if (!string.IsNullOrEmpty(gameIp) && gamePort > 0)
            {
                // Đo TCP Handshake Ping trực tiếp tới IP & Port của Game Server
                var (rtt, ok) = await MeasureTcpPingAsync(gameIp, gamePort, 1200);
                if (ok && rtt > 0) return rtt;
            }

            // 2. Nếu game chưa chạy ➔ Đo TCP/ICMP Ping tới Node IP hoặc targetHost
            var (nodeRtt, nodeOk) = await MeasureTcpPingAsync(host, 2408, 1200);
            if (nodeOk && nodeRtt > 0) return nodeRtt;

            // 3. Fallback ICMP Ping
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(host, 1200);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    // ── Tự động tìm IP & Port Game Server từ TCP Connections ──
    private static (string RemoteIp, int RemotePort) GetActiveGameServerAddress(string processNamesJoined)
    {
        try
        {
            var pNames = processNamesJoined.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var activePids = new HashSet<int>();

            foreach (var pName in pNames)
            {
                var cleanName = pName.Trim();
                if (cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    cleanName = cleanName.Substring(0, cleanName.Length - 4);

                foreach (var proc in System.Diagnostics.Process.GetProcessesByName(cleanName))
                {
                    try { activePids.Add(proc.Id); } catch { }
                }
            }

            if (activePids.Count == 0) return (string.Empty, 0);

            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpConns   = properties.GetActiveTcpConnections();

            foreach (var conn in tcpConns)
            {
                if (conn.State != TcpState.Established) continue;
                var ip = conn.RemoteEndPoint.Address;
                if (System.Net.IPAddress.IsLoopback(ip)) continue;

                string ipStr = ip.ToString();
                // Lọc bỏ IP LAN nội bộ và IP giả lập Fake-IP
                if (ipStr.StartsWith("127.") || ipStr.StartsWith("192.168.") ||
                    ipStr.StartsWith("10.")  || ipStr.StartsWith("198.18.") ||
                    ipStr.StartsWith("172.16.")) continue;

                return (ipStr, conn.RemoteEndPoint.Port);
            }
        }
        catch { }
        return (string.Empty, 0);
    }

    // ── Đo TCP Handshake Ping (Không bị Firewall chặn ICMP) ─────
    private static async Task<(long RttMs, bool Success)> MeasureTcpPingAsync(string host, int port, int timeoutMs)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            sw.Stop();

            if (completedTask == connectTask && client.Connected)
            {
                long ms = sw.ElapsedMilliseconds < 1 ? 1 : sw.ElapsedMilliseconds;
                return (ms, true);
            }
        }
        catch { }
        return (-1, false);
    }

    private async Task<long> MeasureAverageAsync(int count)
    {
        var results = new List<long>();
        for (int i = 0; i < count; i++)
        {
            var ms = await MeasurePingAsync(_targetHost);
            if (ms >= 0) results.Add(ms);
            await Task.Delay(500);
        }
        return results.Count > 0 ? (long)results.Average() : 0;
    }

    private void UpdateHistory(long ping)
    {
        if (PingHistory.Count >= 24)
            PingHistory.RemoveAt(0);
        PingHistory.Add(ping < 0 ? 0 : ping);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _timer.Dispose();
    }
}
