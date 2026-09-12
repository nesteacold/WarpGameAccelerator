using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public sealed record MasqueEndpoint(string Address, int Port, string Network = "h3")
{
    public override string ToString() => $"{(Address.Contains(':') ? $"[{Address}]" : Address)}:{Port} · {(Network == "h2" ? "HTTP/2 TCP" : "HTTP/3 UDP")}";
}

public sealed record MasqueProbeResult(MasqueEndpoint Endpoint, string Summary, bool Success)
{
    public DateTimeOffset? MeasuredAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Colos => System.Text.RegularExpressions.Regex.Matches(Summary, @"colo=([A-Za-z]{3}(?:,[A-Za-z]{3})*)")
        .SelectMany(match => match.Groups[1].Value.Split(',')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    [System.Text.Json.Serialization.JsonIgnore]
    public double? WarmHttpMs
    {
        get
        {
            if (!Success) return null;
            // Read legacy reports too, without requiring a new scan.
            var match = System.Text.RegularExpressions.Regex.Match(Summary, @"HTTP ms:\s*(\d+)\s*/\s*(\d+)\s*/\s*(\d+)");
            if (!match.Success) return null;
            return (double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)) / 2;
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string HttpMsText => WarmHttpMs is { } ms ? $"{ms:0.#} ms" : "—";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySummary => System.Text.RegularExpressions.Regex.Replace(Summary,
        @"HTTP ms:\s*(\d+)\s*/\s*(\d+)\s*/\s*(\d+)",
        "Lần đầu (gồm kết nối): $1 ms · Lần 2: $2 ms · Lần 3: $3 ms")
        + (MeasuredAt is { } time ? $" · {time.ToLocalTime():dd/MM HH:mm}" : " · kết quả cũ");
    public override string ToString() => $"{Endpoint} — {Summary}";
}

public sealed record MasqueProbeHistory(DateTimeOffset MeasuredAt, List<MasqueProbeResult> Results);

/// <summary>Small, serial, real-tunnel experiment. Never changes system routes or account keys.</summary>
public static class MasqueEndpointLab
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpGameAccelerator", "Data");
    private static readonly string SelectionPath = Path.Combine(DataDir, "masque_endpoint_override.json");
    private static readonly string ResultsPath = Path.Combine(DataDir, "masque-endpoint-results.json");
    private static readonly SemaphoreSlim ScanLock = new(1, 1);

    public static MasqueProbeHistory? LoadResults() => File.Exists(ResultsPath)
        ? JsonSerializer.Deserialize<MasqueProbeHistory>(File.ReadAllText(ResultsPath)) : null;

    private static void SaveResults(List<MasqueProbeResult> results)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ResultsPath + ".tmp", JsonSerializer.Serialize(
            new MasqueProbeHistory(DateTimeOffset.Now, results), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(ResultsPath + ".tmp", ResultsPath, true);
    }

    public static MasqueEndpoint? LoadSelection()
    {
        if (!File.Exists(SelectionPath)) return null;
        var value = JsonSerializer.Deserialize<MasqueEndpoint>(File.ReadAllText(SelectionPath));
        if (value == null || !IPAddress.TryParse(value.Address, out _) || value.Port is < 1 or > 65535
            || value.Network is not (null or "h3" or "h2"))
            throw new InvalidDataException("Endpoint MASQUE đã lưu không hợp lệ. Hãy chọn Khôi phục mặc định.");
        return value;
    }

    public static void SaveSelection(MasqueEndpoint? endpoint)
    {
        Directory.CreateDirectory(DataDir);
        if (endpoint == null) { if (File.Exists(SelectionPath)) File.Delete(SelectionPath); return; }
        if (!IPAddress.TryParse(endpoint.Address, out _) || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Endpoint không hợp lệ.");
        File.WriteAllText(SelectionPath + ".tmp", JsonSerializer.Serialize(endpoint));
        File.Move(SelectionPath + ".tmp", SelectionPath, true);
    }

    public static async Task ScanAsync(Action<MasqueProbeResult> report, CancellationToken token,
        bool extended = false, MasqueEndpoint? only = null)
    {
        if (!await ScanLock.WaitAsync(0, token))
            throw new InvalidOperationException("Phép đo trước đang dừng. Hãy thử lại sau vài giây.");
        try { await ScanCoreAsync(report, token, extended, only); }
        finally { ScanLock.Release(); }
    }

    private static async Task ScanCoreAsync(Action<MasqueProbeResult> report, CancellationToken token,
        bool extended, MasqueEndpoint? only)
    {
        var running = Process.GetProcessesByName("mihomo");
        try
        {
            if (running.Length != 0)
                throw new InvalidOperationException("Hãy tắt Boost trước khi đo endpoint.");
        }
        finally { foreach (var process in running) process.Dispose(); }

        var account = await WarpAccountService.GetOrCreateMasqueAccountAsync();
        var endpoints = new List<MasqueEndpoint>();
        if (IPAddress.TryParse(account.ServerIp, out _))
            endpoints.Add(new(account.ServerIp, account.Port > 0 ? account.Port : 443));
        var ports = extended ? new[] { 443, 8443, 500, 1701, 4500, 4443, 8095 } : new[] { 443, 8443, 500 };
        foreach (var address in new[] { "162.159.198.1", "162.159.198.2", "162.159.199.1", "162.159.199.2" })
            foreach (var port in ports) endpoints.Add(new(address, port));
        foreach (var address in new[] { "2606:4700:103::1", "2606:4700:103::2", "2606:4700:104::1", "2606:4700:104::2" })
            foreach (var port in extended ? ports : new[] { 443 }) endpoints.Add(new(address, port));

        if (extended)
        {
            // Bounded samples from consumer pools documented by warpscout/masque.go.
            // These are candidates, not country-specific IPs. Do not sweep arbitrary Cloudflare ranges.
            foreach (var prefix in new[] { "162.159.198", "162.159.199" })
                foreach (var suffix in new[] { 1, 2, 10, 64, 128, 254 })
                    endpoints.Add(new($"{prefix}.{suffix}", 443, "h2"));
            foreach (var prefix in new[] { "2606:4700:103", "2606:4700:104" })
                foreach (var suffix in new[] { "::1", "::2", ":1::1", ":100::1" })
                    endpoints.Add(new(prefix + suffix, 443, "h2"));
        }
        if (only != null) endpoints = [only];

        // Merge with prior measurements, including partial scans, rather than erasing untested rows.
        var results = LoadResults()?.Results ?? new List<MasqueProbeResult>();
        foreach (var endpoint in endpoints.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var result = (await ProbeAsync(account, endpoint, token)) with { MeasuredAt = DateTimeOffset.Now };
            results.RemoveAll(row => row.Endpoint == endpoint);
            results.Add(result);
            SaveResults(results); // Commit every completed endpoint before updating the view.
            report(result);
        }
    }

    private static async Task<MasqueProbeResult> ProbeAsync(WarpMasqueAccountInfo account,
        MasqueEndpoint endpoint, CancellationToken token)
    {
        var core = Path.Combine(Path.GetDirectoryName(DataDir)!, "Core", "mihomo.exe");
        if (!File.Exists(core)) throw new FileNotFoundException("Không tìm thấy Mihomo core.");
        var directory = Path.Combine(DataDir, "MasqueLab", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string Q(string value) => JsonSerializer.Serialize(value);
        var config = $"""
port: {port}
bind-address: 127.0.0.1
allow-lan: false
mode: rule
log-level: silent
ipv6: true
dns:
  enable: false
tun:
  enable: false
proxies:
  - name: PROBE
    type: masque
    network: {endpoint.Network ?? "h3"}
    server: {Q(endpoint.Address)}
    port: {endpoint.Port}
    sni: {Q(account.Server)}
    ip: {Q(account.IPv4)}
    private-key: {Q(account.PrivateKey)}
    public-key: {Q(account.PeerPublicKey)}
    mtu: 1280
    udp: true
rules:
  - MATCH,PROBE
""";
        var configPath = Path.Combine(directory, "config.yaml");
        Process? child = null;
        try
        {
            await File.WriteAllTextAsync(configPath, config, token);
            var start = new ProcessStartInfo(core) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-d"); start.ArgumentList.Add(directory);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(configPath);
            child = Process.Start(start) ?? throw new IOException("Không khởi động được core thử nghiệm.");
            var ready = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (child.HasExited) throw new IOException("Core thử nghiệm thoát sớm.");
                try
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(IPAddress.Loopback, port, token);
                    ready = true; break;
                }
                catch (SocketException) { await Task.Delay(100, token); }
            }
            if (!ready) throw new IOException("Core không mở cổng thử nghiệm.");
            using var handler = new HttpClientHandler
            {
                UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{port}"), AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var samples = new List<long>();
            var colos = new HashSet<string>();
            for (int sample = 0; sample < 3; sample++)
            {
                var timer = Stopwatch.StartNew();
                var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", token);
                timer.Stop();
                var fields = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().Split('=', 2)).Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1]);
                if (!fields.TryGetValue("warp", out var warp) || (warp != "on" && warp != "plus"))
                    throw new IOException("HTTP không xác nhận warp=on/plus.");
                if (!fields.TryGetValue("colo", out var colo)) throw new IOException("Thiếu colo trong trace.");
                colos.Add(colo); samples.Add(timer.ElapsedMilliseconds);
                await Task.Delay(250, token);
            }
            string secondColo;
            try
            {
                // Fixed IPv4 destination: compare against the hostname-based trace.
                // Neither HTTP response alone identifies the QUIC ingress location.
                var secondTrace = await client.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", token);
                secondColo = secondTrace.Split('\n').Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("colo=", StringComparison.Ordinal)) ?? "thiếu colo";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { secondColo = "không đo được"; }
            return new(endpoint, $"www: colo={string.Join(",", colos)} · 1.1.1.1: {secondColo} · HTTP ms: {string.Join(" / ", samples)} · 3/3", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(endpoint, ex is OperationCanceledException ? "Timeout 8 giây" : ex.Message, false);
        }
        finally
        {
            if (child != null)
            {
                try { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
                finally { child.Dispose(); }
            }
            await CleanupProbeDirectoryAsync(directory);
        }
    }

    internal static async Task CleanupProbeDirectoryAsync(string directory)
    {
        // Validate before recursive deletion. Only our unique probe directories are eligible.
        var root = Path.GetFullPath(Path.Combine(DataDir, "MasqueLab"));
        var target = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new ArgumentException("Not a MASQUE probe directory.", nameof(directory));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // Remove credentials first, even if a cache file remains locked by Windows/AV.
                var config = Path.Combine(target, "config.yaml");
                if (File.Exists(config)) File.Delete(config);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    // Cleanup must never discard a successful measurement or abort the scan.
                    Debug.WriteLine($"MASQUE probe cleanup deferred ({Path.GetFileName(target)}): {ex.GetType().Name}");
                    return;
                }
                await Task.Delay(150 * (attempt + 1));
            }
        }
    }
}
