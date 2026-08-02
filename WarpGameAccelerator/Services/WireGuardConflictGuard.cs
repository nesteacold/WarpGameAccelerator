// ============================================================
// Services/WireGuardConflictGuard.cs
// Tạm dừng WireGuard for Windows (dùng cho remote-access VPN cá nhân, KHÔNG
// liên quan tới WARP) trong lúc Boost — 2 driver TUN/WFP (Mihomo + WireGuard
// Windows) cùng chạy có thể xung đột tầng thấp, gây mất kết nối chập chờn.
// Tự trả lại đúng service đã tạm dừng khi Stop Boost.
//
// KHÔNG hardcode tên "wg_server" — tự phát hiện mọi service WireGuard for
// Windows đang chạy (pattern "WireGuardTunnel$*"), phòng trường hợp người
// dùng đổi tên tunnel hoặc có nhiều tunnel.
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public static class WireGuardConflictGuard
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "wg_paused_services.json");

    /// <summary>
    /// Dừng mọi service "WireGuardTunnel$*" đang Running, ghi nhớ ra file để
    /// <see cref="ResumeAsync"/> biết chính xác cái nào cần bật lại.
    ///
    /// CHỦ Ý không tự resume ở lần khởi động app kế tiếp nếu bị bỏ dở (app
    /// crash lúc đang Boost): Mihomo cố ý sống sót sau crash để không ngắt
    /// game (xem MihomoService/CLAUDE.md) — nếu tự resume ngay khi mở lại
    /// app trong khi Mihomo vẫn đang chạy ngầm phục vụ game, sẽ tái tạo đúng
    /// xung đột ban đầu NGAY LÚC đang chơi, còn tệ hơn việc tạm thời không
    /// remote được vào máy.
    /// </summary>
    public static async Task PauseAsync()
    {
        try
        {
            var output = await RunPowerShellAsync(
                "Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Status -eq 'Running' } | Select-Object -ExpandProperty Name");

            var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

            if (names.Count == 0) return;

            foreach (var name in names)
                await RunPowerShellAsync($"Stop-Service -Name '{name}' -Force -ErrorAction SilentlyContinue");

            var dir = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(StateFilePath, JsonSerializer.Serialize(names));

            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Đã tạm dừng: {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Pause lỗi: {ex.Message}");
        }
    }

    /// <summary>Khởi động lại đúng những service đã tạm dừng ở <see cref="PauseAsync"/>.</summary>
    public static async Task ResumeAsync()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;

            var json = await File.ReadAllTextAsync(StateFilePath);
            var names = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

            foreach (var name in names)
                await RunPowerShellAsync($"Start-Service -Name '{name}' -ErrorAction SilentlyContinue");

            File.Delete(StateFilePath);
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Đã khôi phục: {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Resume lỗi: {ex.Message}");
        }
    }

    private static async Task<string> RunPowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
