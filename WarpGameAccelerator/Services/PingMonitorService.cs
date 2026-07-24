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

    public async Task<long> MeasurePingAsync(string host)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(host, 1500);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch { return -1; }
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
