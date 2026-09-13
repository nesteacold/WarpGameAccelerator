using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Trạng thái đo colo gần nhất của một candidate MASQUE đang sống trong core mihomo
/// hiện tại. <c>Colo</c>/<c>LatencyMs</c> là null khi KHÔNG đo được — không bao giờ
/// bịa/nội suy (xem CLAUDE.md mục "Chỉ số hiển thị: KHÔNG được bịa"); UI phải hiện
/// "chưa đo"/"chưa xác thực" ứng với null, không hiện số cũ như thể vừa đo xong.
/// </summary>
public sealed record CandidateColoStatus(
    string ProxyName,
    MasqueEndpoint Endpoint,
    string? Colo,
    double? LatencyMs,
    DateTimeOffset? VerifiedAtUtc,
    bool IsCurrentSelection);

/// <summary>
/// Sweep THỤ ĐỘNG các candidate MASQUE đang sống trong core mihomo hiện tại — chỉ
/// có ý nghĩa khi MultiCandidateSettings.IsEnabled() VÀ MihomoService.ActiveMasqueControl
/// khác null (đã Boost ở mode DirectMasqueBeta với tính năng bật). Dùng PROBE-PATH
/// (proxy-group riêng, tách khỏi GAME-PATH bằng rule SRC-PORT) để đo colo từng
/// candidate mà KHÔNG đụng vào đường traffic thật của game — GAME-PATH không bị
/// PUT trong toàn bộ service này.
///
/// Mô hình theo PingMonitorService: singleton (DI), sự kiện cho UI subscribe, không
/// phải utility tĩnh — vì cần giữ trạng thái "kết quả đo gần nhất mỗi candidate"
/// xuyên suốt vòng đời Boost.
/// </summary>
public sealed class MasqueTunnelSweepService
{
    private readonly MihomoService _mihomo;
    private readonly object _lock = new();
    private readonly Dictionary<string, CandidateColoStatus> _latest = new();
    private CancellationTokenSource? _reverifyCts;
    private static int _srcPortCursor;

    public event EventHandler<IReadOnlyList<CandidateColoStatus>>? SweepUpdated;

    public MasqueTunnelSweepService(MihomoService mihomo)
    {
        _mihomo = mihomo;
    }

    public IReadOnlyList<CandidateColoStatus> LatestSnapshot()
    {
        lock (_lock) return _latest.Values.ToList();
    }

    /// <summary>
    /// Đo colo của MỘT candidate: PUT PROBE-PATH → proxyName, rồi GET
    /// https://www.cloudflare.com/cdn-cgi/trace xuyên qua core mihomo đang chạy
    /// (mixed-port 127.0.0.1:7890), ép socket nguồn vào 1 cổng trong dải
    /// MihomoService.ProbeSrcPortRangeStart..End để rule "SRC-PORT,...,PROBE-PATH"
    /// khớp đúng request này (không phải MATCH/PROCESS-NAME của game).
    ///
    /// KHÔNG BAO GIỜ bịa Colo khi lỗi — trả về status với Colo=null, giữ nguyên tinh
    /// thần "hiện không đo được" thay vì số cũ/nội suy.
    /// </summary>
    public async Task<CandidateColoStatus?> ProbeOneAsync(string proxyName, CancellationToken ct)
    {
        var session = _mihomo.ActiveMasqueControl;
        if (session == null || !session.Candidates.TryGetValue(proxyName, out var endpoint))
            return null;

        bool selected = await session.Client.SelectAsync("PROBE-PATH", proxyName);
        if (!selected)
        {
            var failed = new CandidateColoStatus(proxyName, endpoint, null, null, DateTimeOffset.UtcNow, false);
            Record(failed);
            return failed;
        }

        string? colo = null;
        double? latencyMs = null;
        try
        {
            int srcPort = NextSrcPort();
            using var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = new WebProxy("http://127.0.0.1:7890"),
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        // Bind cổng nguồn vào dải riêng của PROBE — đây là cách DUY
                        // NHẤT để mihomo (rule engine phía server-side của kết nối)
                        // phân biệt được probe của service này với traffic game thật
                        // đi qua cùng mixed-port. Xem SRC-PORT rule trong MihomoService.
                        socket.Bind(new IPEndPoint(IPAddress.Loopback, srcPort));
                        await socket.ConnectAsync(context.DnsEndPoint, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var sw = Stopwatch.StartNew();
            var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct);
            sw.Stop();
            latencyMs = sw.Elapsed.TotalMilliseconds;

            // Cùng cách parse "colo=" đã dùng trong MasqueEndpointLab.ProbeAsync —
            // không viết lại regex/logic khác.
            var coloLine = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("colo=", StringComparison.Ordinal));
            colo = coloLine?["colo=".Length..];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            colo = null;
            latencyMs = null;
        }

        bool isCurrent = false;
        try
        {
            var currentGame = await session.Client.GetSelectedAsync("GAME-PATH");
            isCurrent = currentGame == proxyName;
        }
        catch { }

        var result = new CandidateColoStatus(proxyName, endpoint, colo, latencyMs, DateTimeOffset.UtcNow, isCurrent);
        Record(result);
        return result;
    }

    /// <summary>Quét tuần tự TẤT CẢ candidate hiện biết, phát SweepUpdated sau mỗi candidate.</summary>
    public async Task SweepAllAsync(CancellationToken ct)
    {
        var session = _mihomo.ActiveMasqueControl;
        if (session == null) return;

        foreach (var proxyName in session.Candidates.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await ProbeOneAsync(proxyName, ct);
            SweepUpdated?.Invoke(this, LatestSnapshot());
        }
    }

    /// <summary>
    /// Re-verify định kỳ, NHẸ: chỉ đo lại candidate ĐANG active trên GAME-PATH — không
    /// có tín hiệu đáng tin nào báo "tunnel đã âm thầm reconnect" (giống hạn chế đã ghi
    /// ở LastGameDialFailureUtc), nên đây là thay thế chủ động: gắn VerifiedAtUtc vào
    /// mỗi kết quả để UI hiện "xác thực lúc HH:mm:ss" và để lộ rõ khi dữ liệu đã cũ,
    /// thay vì giấu đi.
    /// </summary>
    public Task StartPeriodicReverifyAsync(TimeSpan interval)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _reverifyCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(interval, cts.Token);
                    var session = _mihomo.ActiveMasqueControl;
                    if (session == null) continue;

                    var current = await session.Client.GetSelectedAsync("GAME-PATH");
                    if (string.IsNullOrEmpty(current)) continue;

                    await ProbeOneAsync(current, cts.Token);
                    SweepUpdated?.Invoke(this, LatestSnapshot());
                }
            }
            catch (OperationCanceledException) { }
            catch { /* vòng lặp nền — không để lỗi 1 lần làm chết hẳn re-verify */ }
        }, cts.Token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _reverifyCts?.Cancel();
        _reverifyCts = null;
    }

    /// <summary>
    /// Cập nhật NGAY cờ IsCurrentSelection sau khi SwitchGamePathAsync thành công, KHÔNG
    /// chờ lần ProbeOneAsync/sweep kế tiếp — thiếu bước này thì UI vẫn hiện tunnel CŨ là
    /// "đang dùng" ngay sau khi bấm "Chọn" (đổi thật ở mihomo nhưng danh sách hiển thị vẫn
    /// là snapshot trước đó), trông như nút không phản hồi. Không tự đo lại colo ở đây —
    /// chỉ cờ "đang dùng" đổi ngay; số đo colo của candidate mới vẫn giữ nguyên từ lần
    /// sweep gần nhất (hoặc "chưa đo" nếu chưa từng), không bịa số mới.
    /// </summary>
    public void MarkSelected(string proxyName)
    {
        lock (_lock)
        {
            var keys = _latest.Keys.ToList();
            foreach (var key in keys)
            {
                var s = _latest[key];
                bool shouldBeCurrent = key == proxyName;
                if (s.IsCurrentSelection != shouldBeCurrent)
                    _latest[key] = s with { IsCurrentSelection = shouldBeCurrent };
            }
        }
        SweepUpdated?.Invoke(this, LatestSnapshot());
    }

    private static int NextSrcPort()
    {
        int rangeSize = MihomoService.ProbeSrcPortRangeEnd - MihomoService.ProbeSrcPortRangeStart + 1;
        int offset = Interlocked.Increment(ref _srcPortCursor) % rangeSize;
        if (offset < 0) offset += rangeSize;
        return MihomoService.ProbeSrcPortRangeStart + offset;
    }

    private void Record(CandidateColoStatus status)
    {
        lock (_lock) _latest[status.ProxyName] = status;
    }
}
