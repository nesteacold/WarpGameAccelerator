using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Trạng thái đo colo gần nhất của một candidate MASQUE/Worker đang sống trong core mihomo
/// hiện tại. <c>Colo</c>/<c>LatencyMs</c>/<c>TargetRttMs</c> là null khi KHÔNG đo được — không
/// bao giờ bịa/nội suy (xem CLAUDE.md mục "Chỉ số hiển thị: KHÔNG được bịa"); UI phải hiện
/// "chưa đo"/"chưa xác thực" ứng với null, không hiện số cũ như thể vừa đo xong.
/// </summary>
public sealed record CandidateColoStatus(
    string ProxyName,
    MasqueEndpoint Endpoint,
    string? Colo,
    double? LatencyMs,
    DateTimeOffset? VerifiedAtUtc,
    bool IsCurrentSelection,
    double? TargetRttMs = null);

/// <summary>
/// Sweep THỤ ĐỘNG các candidate MASQUE đang sống trong core mihomo hiện tại + Auto-Hunt HKG
/// (Bước 2) + Hot-Standby Failover kiểu GearUP (Bước 3).
///
/// Chỉ có ý nghĩa khi MultiCandidateSettings.IsEnabled() VÀ MihomoService.ActiveMasqueControl
/// khác null (đã Boost ở mode DirectMasqueBeta). Dùng PROBE-PATH (proxy-group riêng, tách khỏi
/// GAME-PATH bằng rule SRC-PORT 31337-31347) để đo từng candidate mà KHÔNG đụng vào đường
/// traffic thật của game; khi Auto-Hunt hoặc Failover quyết định chuyển tuyến thì chỉ gọi
/// PUT /proxies/GAME-PATH (không restart mihomo → không cắt client đang sống trong map).
/// </summary>
public sealed class MasqueTunnelSweepService
{
    private const string TaiwanRefUrl = "https://www.ntu.edu.tw/";
    private static readonly TimeSpan FailoverCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DialBurstWindow = TimeSpan.FromSeconds(20);
    private const int DialBurstTriggerCount = 2;

    private readonly MihomoService _mihomo;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly Dictionary<string, CandidateColoStatus> _latest = new();
    private readonly Queue<DateTimeOffset> _recentGameDialFails = new();

    private CancellationTokenSource? _reverifyCts;
    private SemaphoreSlim? _failoverWakeSignal;
    private Action<string>? _dialFailHandler;
    private DateTimeOffset _lastFailoverSwitchUtc = DateTimeOffset.MinValue;
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

    /// <summary>Đếm số slot HKG/TPE dự phòng khác đang có sẵn trong snapshot gần nhất.</summary>
    public int HotStandbyCount()
    {
        lock (_lock)
            return _latest.Values.Count(s => !s.IsCurrentSelection && RoutingCandidateScorer.IsPreferredRegion(s.Colo));
    }

    /// <summary>Xoá cache cũ khi bắt đầu phiên Boost mới.</summary>
    public void ResetSessionCache()
    {
        lock (_lock)
        {
            _latest.Clear();
            _recentGameDialFails.Clear();
            _lastFailoverSwitchUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Đo colo của MỘT candidate: PUT PROBE-PATH → proxyName, rồi GET
    /// https://www.cloudflare.com/cdn-cgi/trace xuyên qua core mihomo đang chạy
    /// (mixed-port 127.0.0.1:7890), ép socket nguồn vào 1 cổng trong dải
    /// MihomoService.ProbeSrcPortRangeStart..End để rule "SRC-PORT,...,PROBE-PATH"
    /// khớp đúng request này (không phải MATCH/PROCESS-NAME của game).
    ///
    /// Khi <paramref name="measureTargetRtt"/> bật và colo thuộc vùng HKG/TPE, đo thêm
    /// 1-RTT thật (request #2 trên kết nối HTTPS keep-alive) tới đích tham chiếu Taiwan
    /// (www.ntu.edu.tw) để phân biệt tốc độ thật giữa các slot HKG.
    ///
    /// KHÔNG BAO GIỜ bịa Colo/RTT khi lỗi — trả về null, giữ nguyên tinh thần "không đo được".
    /// </summary>
    public async Task<CandidateColoStatus?> ProbeOneAsync(
        string proxyName,
        CancellationToken ct,
        bool measureTargetRtt = false)
    {
        var session = _mihomo.ActiveMasqueControl;
        if (session == null || !session.Candidates.TryGetValue(proxyName, out var endpoint))
            return null;

        await _probeGate.WaitAsync(ct);
        try
        {
            session = _mihomo.ActiveMasqueControl;
            if (session == null || !session.Candidates.TryGetValue(proxyName, out endpoint))
                return null;

            bool selected = await session.Client.SelectAsync("PROBE-PATH", proxyName);
            if (!selected)
            {
                var failed = new CandidateColoStatus(proxyName, endpoint, null, null, DateTimeOffset.UtcNow, false, null);
                Record(failed);
                return failed;
            }

            string? colo = null;
            double? latencyMs = null;
            double? targetRttMs = null;

            try
            {
                using var handler = CreateProbeHttpHandler();
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
                var sw = Stopwatch.StartNew();
                var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct);
                sw.Stop();
                latencyMs = sw.Elapsed.TotalMilliseconds;

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

            // Bước 2: với slot HKG/TPE, đo 1-RTT thật tới đích Taiwan (www.ntu.edu.tw) bằng
            // 2 lượt HEAD liên tiếp trên cùng socket keep-alive (lượt 2 bỏ qua chi phí TLS handshake).
            if (measureTargetRtt && RoutingCandidateScorer.IsPreferredRegion(colo))
            {
                targetRttMs = await MeasureTaiwanKeepAlive1RttAsync(ct);
            }

            bool isCurrent = false;
            try
            {
                var currentGame = await session.Client.GetSelectedAsync("GAME-PATH");
                isCurrent = currentGame == proxyName;
            }
            catch { }

            var result = new CandidateColoStatus(
                proxyName, endpoint, colo, latencyMs, DateTimeOffset.UtcNow, isCurrent, targetRttMs);
            Record(result);
            return result;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>Quét tuần tự TẤT CẢ candidate hiện biết, phát SweepUpdated sau mỗi candidate.</summary>
    public async Task SweepAllAsync(CancellationToken ct, bool measureTargetRtt = false)
    {
        var session = _mihomo.ActiveMasqueControl;
        if (session == null) return;

        foreach (var proxyName in session.Candidates.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await ProbeOneAsync(proxyName, ct, measureTargetRtt);
            SweepUpdated?.Invoke(this, LatestSnapshot());
        }
    }

    /// <summary>
    /// Bước 2 (Auto-Hunt & Auto-Select HKG): quét toàn bộ pool ngay khi Boost vừa lên,
    /// chọn slot HKG/TPE có 1-RTT tốt nhất và tự động chuyển GAME-PATH qua REST API
    /// (không restart mihomo). Trả về candidate đã chọn nếu thành công.
    /// </summary>
    public async Task<CandidateColoStatus?> AutoHuntAndSelectBestAsync(CancellationToken ct)
    {
        var session = _mihomo.ActiveMasqueControl;
        if (session == null) return null;

        // Chờ controller mihomo thực sự nhận request (tối đa 4s sau khi process vừa spawn).
        string? currentName = null;
        for (int attempt = 0; attempt < 16 && currentName == null; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            currentName = await session.Client.GetSelectedAsync("GAME-PATH");
            if (currentName == null)
                await Task.Delay(250, ct);
        }
        if (currentName == null) return null;

        ResetSessionCache();
        await SweepAllAsync(ct, measureTargetRtt: true);

        var snapshot = LatestSnapshot();
        var current = snapshot.FirstOrDefault(s => string.Equals(s.ProxyName, currentName, StringComparison.Ordinal));
        var best = RoutingCandidateScorer.PickBest(snapshot);
        int hkgCount = snapshot.Count(s => RoutingCandidateScorer.IsPreferredRegion(s.Colo));

        if (best != null && RoutingCandidateScorer.ShouldUpgradeOnAutoHunt(current, best))
        {
            bool switched = await _mihomo.SwitchGamePathAsync(best.ProxyName);
            if (switched)
            {
                _mihomo.UpdateActiveCandidateDisplay(best.ProxyName, best.Colo);
                MarkSelected(best.ProxyName);
                string rttNote = best.TargetRttMs is { } tw ? $"TW 1-RTT={tw:0.0}ms" : $"CF={best.LatencyMs:0.0}ms";
                DiagnosticLogService.Trace(
                    $"[AutoHunt] Chọn {best.ProxyName} ({best.Endpoint}, colo={best.Colo}, {rttNote}) " +
                    $"từ {snapshot.Count} slot ({hkgCount} slot HKG/TPE, giữ {Math.Max(0, hkgCount - 1)} hot-standby).");
                return best with { IsCurrentSelection = true };
            }
        }
        else if (current != null)
        {
            _mihomo.UpdateActiveCandidateDisplay(current.ProxyName, current.Colo);
            MarkSelected(current.ProxyName);
            DiagnosticLogService.Trace(
                $"[AutoHunt] Giữ tuyến hiện tại {current.ProxyName} (colo={current.Colo ?? "?"}, {hkgCount}/{snapshot.Count} slot HKG/TPE).");
        }

        return current;
    }

    /// <summary>
    /// Bước 3 (GearUP-style Hot-Standby Failover): vừa re-verify định kỳ tuyến đang active,
    /// vừa lắng nghe sự kiện lỗi dial game từ MihomoService. Khi tuyến hiện tại hỏng hoặc rơi
    /// khỏi HKG/TPE trong khi có slot dự phòng khoẻ → probe xác nhận sống rồi chuyển GAME-PATH
    /// ngay bằng REST API (không restart mihomo, giữ nguyên các client đang ở trong map).
    /// </summary>
    public Task StartAutoFailoverMonitor(TimeSpan checkInterval)
    {
        Stop();
        var cts = new CancellationTokenSource();
        var wake = new SemaphoreSlim(0, 1);
        _reverifyCts = cts;
        _failoverWakeSignal = wake;

        Action<string> onDialFail = _ =>
        {
            bool shouldWake = false;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                _recentGameDialFails.Enqueue(now);
                while (_recentGameDialFails.Count > 0 && now - _recentGameDialFails.Peek() > DialBurstWindow)
                    _recentGameDialFails.Dequeue();
                if (_recentGameDialFails.Count >= DialBurstTriggerCount)
                    shouldWake = true;
            }
            if (shouldWake)
            {
                try
                {
                    if (wake.CurrentCount == 0) wake.Release();
                }
                catch { }
            }
        };
        _dialFailHandler = onDialFail;
        _mihomo.GameDialFailureDetected += onDialFail;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    bool wokenByDialBurst = await wake.WaitAsync(checkInterval, cts.Token);
                    var session = _mihomo.ActiveMasqueControl;
                    if (session == null) continue;

                    var currentName = await session.Client.GetSelectedAsync("GAME-PATH");
                    if (string.IsNullOrEmpty(currentName)) continue;

                    var currentStatus = await ProbeOneAsync(currentName, cts.Token, measureTargetRtt: true);
                    SweepUpdated?.Invoke(this, LatestSnapshot());

                    int recentFails;
                    lock (_lock)
                    {
                        var now = DateTimeOffset.UtcNow;
                        while (_recentGameDialFails.Count > 0 && now - _recentGameDialFails.Peek() > DialBurstWindow)
                            _recentGameDialFails.Dequeue();
                        recentFails = _recentGameDialFails.Count;
                    }

                    bool currentUnhealthy = currentStatus == null
                                            || string.IsNullOrEmpty(currentStatus.Colo)
                                            || (wokenByDialBurst && recentFails >= DialBurstTriggerCount);
                    bool lostPreferredRegion = currentStatus != null
                                               && !RoutingCandidateScorer.IsPreferredRegion(currentStatus.Colo);

                    if (!currentUnhealthy && !lostPreferredRegion)
                        continue;

                    if (DateTimeOffset.UtcNow - _lastFailoverSwitchUtc < FailoverCooldown)
                        continue;

                    // Thăm dò sống các candidate dự phòng theo thứ tự ưu tiên (HKG/TPE trước)
                    // để chắc chắn chỉ chuyển sang slot THỰC SỰ sống ngay lúc này.
                    var orderedStandbys = BuildStandbyProbeOrder(session, currentName);
                    CandidateColoStatus? healthyStandby = null;

                    foreach (var candName in orderedStandbys)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var probed = await ProbeOneAsync(candName, cts.Token, measureTargetRtt: true);
                        SweepUpdated?.Invoke(this, LatestSnapshot());
                        if (probed == null || string.IsNullOrEmpty(probed.Colo))
                            continue;

                        if (RoutingCandidateScorer.ShouldFailover(
                                currentStatus, currentUnhealthy, probed,
                                DateTimeOffset.UtcNow, _lastFailoverSwitchUtc, FailoverCooldown))
                        {
                            healthyStandby = probed;
                            // Gặp slot HKG/TPE khoẻ là chốt ngay; nếu chỉ thấy slot thường thì tiếp tục
                            // tìm xem có slot HKG nào phía sau không trước khi chốt.
                            if (RoutingCandidateScorer.IsPreferredRegion(probed.Colo))
                                break;
                        }
                    }

                    if (healthyStandby != null)
                    {
                        bool switched = await _mihomo.SwitchGamePathAsync(healthyStandby.ProxyName);
                        if (switched)
                        {
                            _lastFailoverSwitchUtc = DateTimeOffset.UtcNow;
                            lock (_lock) { _recentGameDialFails.Clear(); }
                            _mihomo.UpdateActiveCandidateDisplay(healthyStandby.ProxyName, healthyStandby.Colo);
                            MarkSelected(healthyStandby.ProxyName);

                            string reason = currentUnhealthy
                                ? (recentFails >= DialBurstTriggerCount
                                    ? $"{recentFails} lỗi dial game trong {DialBurstWindow.TotalSeconds:0}s"
                                    : "tuyến hiện tại không phản hồi")
                                : $"nâng cấp vùng {currentStatus?.Colo ?? "?"} → {healthyStandby.Colo}";
                            DiagnosticLogService.Trace(
                                $"[AutoFailover] Chuyển GAME-PATH: {currentName} → {healthyStandby.ProxyName} " +
                                $"(colo={healthyStandby.Colo}, TW={healthyStandby.TargetRttMs?.ToString("0.0") ?? "?"}ms) — {reason}");
                        }
                    }
                    else if (currentUnhealthy)
                    {
                        DiagnosticLogService.Trace(
                            $"[AutoFailover] Tuyến {currentName} gặp lỗi nhưng không có slot dự phòng khoẻ hơn — giữ nguyên, không restart mihomo.");
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (cts.IsCancellationRequested) { break; }
                catch { /* vòng lặp nền — lỗi 1 lượt không được làm chết hẳn monitor */ }
            }
        }, cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-verify định kỳ, NHẸ: chỉ đo lại candidate ĐANG active trên GAME-PATH — dùng khi
    /// người dùng tắt Auto-Failover nhưng vẫn bật Multi-Candidate.
    /// </summary>
    public Task StartPeriodicReverifyAsync(TimeSpan interval)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _reverifyCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cts.Token);
                    var session = _mihomo.ActiveMasqueControl;
                    if (session == null) continue;

                    var current = await session.Client.GetSelectedAsync("GAME-PATH");
                    if (string.IsNullOrEmpty(current)) continue;

                    await ProbeOneAsync(current, cts.Token, measureTargetRtt: true);
                    SweepUpdated?.Invoke(this, LatestSnapshot());
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch { /* vòng lặp nền — lỗi 1 lượt không được làm chết hẳn re-verify */ }
            }
        }, cts.Token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_dialFailHandler != null)
        {
            _mihomo.GameDialFailureDetected -= _dialFailHandler;
            _dialFailHandler = null;
        }
        var oldCts = _reverifyCts;
        _reverifyCts = null;
        try { oldCts?.Cancel(); } catch { }
        try { oldCts?.Dispose(); } catch { }

        var oldWake = _failoverWakeSignal;
        _failoverWakeSignal = null;
        try { oldWake?.Dispose(); } catch { }
    }

    /// <summary>
    /// Cập nhật NGAY cờ IsCurrentSelection sau khi SwitchGamePathAsync thành công, KHÔNG
    /// chờ lần ProbeOneAsync/sweep kế tiếp.
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

    private List<string> BuildStandbyProbeOrder(MasqueControlSession session, string currentName)
    {
        Dictionary<string, CandidateColoStatus> cached;
        lock (_lock) cached = new Dictionary<string, CandidateColoStatus>(_latest);

        return session.Candidates.Keys
            .Where(name => !string.Equals(name, currentName, StringComparison.Ordinal))
            .OrderByDescending(name => cached.TryGetValue(name, out var s) ? RoutingCandidateScorer.ColoTier(s.Colo) : 1)
            .ThenBy(name => cached.TryGetValue(name, out var s) ? RoutingCandidateScorer.EffectiveLatencyMs(s) : double.MaxValue)
            .ToList();
    }

    private static async Task<double?> MeasureTaiwanKeepAlive1RttAsync(CancellationToken ct)
    {
        try
        {
            using var handler = CreateProbeHttpHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            // Lượt 1 dựng TCP + TLS handshake trên kết nối keep-alive.
            using (var req1 = new HttpRequestMessage(HttpMethod.Head, TaiwanRefUrl))
            using (var resp1 = await client.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp1.IsSuccessStatusCode) return null;
            }
            // Lượt 2 tái sử dụng chính socket TLS đã mở → đo đúng 1-RTT đầu-cuối tới Taiwan.
            var sw = Stopwatch.StartNew();
            using (var req2 = new HttpRequestMessage(HttpMethod.Head, TaiwanRefUrl))
            using (var resp2 = await client.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                sw.Stop();
                return resp2.IsSuccessStatusCode ? sw.Elapsed.TotalMilliseconds : null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static SocketsHttpHandler CreateProbeHttpHandler()
    {
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy("http://127.0.0.1:7890"),
            ConnectCallback = async (context, token) =>
            {
                // Rút cổng mới cho MỖI lần Connect và thử lại nếu đụng TIME_WAIT (WSAEADDRINUSE 10048)
                // trên loopback 127.0.0.1:7890. Dải 200 cổng (31337..31536) đủ cho nhiều đợt quét liên tiếp.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int srcPort = NextSrcPort();
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.NoDelay = true;
                        socket.Bind(new IPEndPoint(IPAddress.Loopback, srcPort));
                        await socket.ConnectAsync(context.DnsEndPoint, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException se) when (se.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 7)
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }
        };
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
