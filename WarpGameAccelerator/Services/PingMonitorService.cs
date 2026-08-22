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
                RecordSample(ping >= 0);
                UpdateHistory(ping);

                var stats = new PingStats
                {
                    CurrentPingMs    = ping,
                    BaselinePingMs   = _baselinePingMs,
                    PacketLossPercent = CurrentLossPercent(),
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

    /// <summary>
    /// Đo RTT ICMP THẬT tới <paramref name="host"/> (endpoint edge WARP).
    /// Trả về -1 nếu không đo được. KHÔNG bịa số.
    ///
    /// ĐÂY KHÔNG PHẢI PING TỚI SERVER GAME — và cũng không thể là như vậy:
    /// khi TUN bật, ICMP tới đích qua tunnel do mihomo giả lập, còn TCP-connect
    /// hoàn tất ở userspace (~1.6ms đo được) trước khi mihomo dial thật, nên cả
    /// hai đều không cho RTT thật tới server game. Server game (cổng 4000) lại
    /// không gửi byte nào khi vừa kết nối nên cũng không đo được qua byte đầu.
    ///
    /// Vì vậy: ô PING trên Dashboard hiển thị RTT tới EDGE (có nhãn rõ), còn
    /// tình trạng đường tới server game lấy từ MihomoService.LastGameDialFailureUtc.
    ///
    /// Bản trước gọi CloudflareNodeService.PingNodeAsync ở đây và return ngay;
    /// hàm đó luôn trả > 0 (kể cả nhánh catch bịa 36/47/58 + Random) nên nhánh
    /// đo thật bên dưới thực tế KHÔNG BAO GIỜ chạy.
    /// </summary>
    public async Task<long> MeasurePingAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return -1;

        try
        {
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

    /// <summary>
    /// Loss THẬT tính trên cửa sổ mẫu gần nhất (mỗi mẫu = 1 lần ICMP tới edge).
    /// Bản trước gán 0%% hoặc 100%% từ MỘT mẫu duy nhất, mà mẫu đó lại luôn > 0
    /// vì lấy từ hàm bịa số ⇒ ô LOSS luôn hiển thị 0.0%% bất kể thực tế.
    ///
    /// Đây là loss tới EDGE, không phải loss tới server game (xem MeasurePingAsync).
    /// </summary>
    private double CurrentLossPercent()
    {
        lock (_sampleLock)
        {
            // Chua co mau nao => KHONG duoc tra 0.0 (nhu the la bao "khong mat goi"
            // trong khi thuc te chua do gi). Tra -1 de UI hien "khong do duoc".
            if (_recentSamples.Count == 0) return -1.0;
            int lost = _recentSamples.Count(ok => !ok);
            return 100.0 * lost / _recentSamples.Count;
        }
    }

    private const int LossWindowSamples = 20;
    private readonly object _sampleLock = new();
    private readonly Queue<bool> _recentSamples = new();

    private void RecordSample(bool ok)
    {
        lock (_sampleLock)
        {
            _recentSamples.Enqueue(ok);
            while (_recentSamples.Count > LossWindowSamples) _recentSamples.Dequeue();
        }
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
