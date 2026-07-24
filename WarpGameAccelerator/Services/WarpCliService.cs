// ============================================================
// Services/WarpCliService.cs — Thực thi lệnh warp-cli ngầm
// ============================================================
using System.Diagnostics;

namespace WarpGameAccelerator.Services;

public class WarpCliService : IWarpService
{
    private const string WarpCli = "warp-cli";

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string arguments, int timeoutMs = 10_000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = WarpCli,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WindowStyle            = ProcessWindowStyle.Hidden
            }
        };

        try
        {
            proc.Start();

            // WaitForExitAsync returns Task (not Task<T>) — cannot use in WhenAll with Task<string>
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask  = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { proc.Kill(entireProcessTree: true); }

            return (proc.ExitCode,
                    await outputTask,
                    await errorTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    public async Task<bool> IsInstalledAsync()
    {
        try
        {
            var (code, _, _) = await RunAsync("--version", 3_000);
            return code == 0;
        }
        catch { return false; }
    }

    public async Task<WarpStatus> GetStatusAsync()
    {
        var (code, output, _) = await RunAsync("status");
        if (code != 0) return WarpStatus.Unknown;

        var lower = output.ToLowerInvariant();
        if (lower.Contains("connected"))    return WarpStatus.Connected;
        if (lower.Contains("connecting"))   return WarpStatus.Connecting;
        if (lower.Contains("disconnected")) return WarpStatus.Disconnected;
        return WarpStatus.Unknown;
    }

    public async Task<bool> ConnectAsync()
    {
        // Cú pháp mới của Cloudflare WARP (2024+)
        await RunAsync("mode proxy", 3_000);
        await RunAsync("proxy port 40000", 3_000);

        // Cú pháp cũ (dành cho các bản WARP cũ hơn) fallback
        await RunAsync("set-mode proxy", 3_000);
        await RunAsync("set-proxy-port 40000", 3_000);

        var (code, _, _) = await RunAsync("connect", 15_000);
        return code == 0;
    }

    public async Task<bool> DisconnectAsync()
    {
        var (code, _, _) = await RunAsync("disconnect", 10_000);
        return code == 0;
    }

    public async Task<bool> AddSplitTunnelProcessAsync(string processName)
    {
        // warp-cli split-tunnel process add <name>
        var (code, _, _) = await RunAsync(
            $"split-tunnel process add {processName}", 5_000);
        return code == 0;
    }

    public async Task<bool> ClearSplitTunnelAsync()
    {
        // Xóa toàn bộ rules để reset về default
        var (code, _, _) = await RunAsync("split-tunnel process remove-all", 5_000);
        return code == 0;
    }
}
