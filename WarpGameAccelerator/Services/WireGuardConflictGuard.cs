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
using System.Linq;
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
            // Loại trừ tunnel người dùng tự khai là WireGuard SERVER cho kênh cá
            // nhân (Dev Panel) đang chạy CHUNG máy với Boost — xem comment ở
            // PersonalVpnConfig.ExcludedTunnelServiceName. Người dùng thật có
            // server ở máy khác thì field này rỗng, không ảnh hưởng gì.
            var excluded = PersonalVpnService.GetExcludedTunnelServiceName();
            excluded = string.IsNullOrWhiteSpace(excluded) ? null : excluded.Trim();

            // Đảm bảo tunnel loại trừ ĐÃ chạy TRƯỚC khi mihomo khởi động — xác
            // nhận thực nghiệm: nếu mihomo lên trước, Wintun driver của
            // WireGuard-for-Windows không tạo được adapter, service báo lỗi
            // "file đang được dùng bởi process khác" và không bao giờ Running
            // được nữa cho tới khi mihomo bị tắt. Thứ tự an toàn duy nhất: tunnel
            // server lên trước, mihomo lên sau.
            if (excluded != null)
                await EnsureExcludedTunnelRunningAsync(excluded);

            var output = await RunPowerShellAsync(
                "Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Status -eq 'Running' } | Select-Object -ExpandProperty Name");

            var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

            if (excluded != null)
            {
                var before = names.Count;
                names = names.Where(n => !n.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                    && !n.Equals($"WireGuardTunnel${excluded}", StringComparison.OrdinalIgnoreCase)).ToList();
                if (names.Count < before)
                    DiagnosticLogService.Trace($"[WireGuardConflictGuard] Loại trừ '{excluded}' theo cấu hình Dev Panel — không tạm dừng.");
            }

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

    /// <summary>
    /// Nếu tunnel loại trừ đang Stopped, tự Start-Service và chờ tối đa ~5s
    /// cho nó Running trước khi trả về — mihomo chỉ khởi động SAU lệnh gọi
    /// này (xem <see cref="PauseAsync"/>), đảm bảo đúng thứ tự an toàn.
    /// Không throw nếu thất bại — chỉ log, để không chặn hẳn việc Boost game
    /// (chấp nhận rủi ro xung đột còn hơn không Boost được).
    /// </summary>
    private static async Task EnsureExcludedTunnelRunningAsync(string excluded)
    {
        var serviceName = excluded.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)
            ? excluded
            : $"WireGuardTunnel${excluded}";

        var status = (await RunPowerShellAsync(
            $"Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Status")).Trim();

        if (string.IsNullOrEmpty(status))
        {
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Tunnel loại trừ '{excluded}' không tồn tại — bỏ qua.");
            return;
        }

        if (status.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureNatForInterfaceAsync(excluded);
            return;
        }

        DiagnosticLogService.Trace($"[WireGuardConflictGuard] Tunnel loại trừ '{excluded}' đang {status} — tự khởi động trước khi mihomo chạy.");
        await RunPowerShellAsync($"Start-Service -Name '{serviceName}' -ErrorAction SilentlyContinue");

        for (int i = 0; i < 20; i++)
        {
            var current = (await RunPowerShellAsync(
                $"Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Status")).Trim();
            if (current.Equals("Running", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLogService.Trace($"[WireGuardConflictGuard] Đã tự khởi động '{excluded}' thành công.");
                // Khi interface bị destroy lúc tunnel Stopped, Windows tự huỷ
                // theo binding NetNat gắn với nó (đã xác nhận thực nghiệm: WS4W
                // báo "NAT is not enabled" ngay sau khi tunnel bị dừng) — tạo
                // lại nếu thiếu, không cần bấm tay "Enable NAT" mỗi lần nữa.
                await EnsureNatForInterfaceAsync(excluded);
                return;
            }
            await Task.Delay(250);
        }

        DiagnosticLogService.Trace($"[WireGuardConflictGuard] Không thể tự khởi động '{excluded}' — mihomo vẫn sẽ tiếp tục chạy, có thể xung đột.");
    }

    /// <summary>
    /// Đảm bảo IP forwarding bật + NetNat object đúng subnet của interface
    /// WireGuard tồn tại VÀ hoạt động thật — subnet tự tính từ IP/PrefixLength
    /// thật của chính interface (KHÔNG hardcode dải mạng).
    ///
    /// LUÔN xoá rồi tạo lại NAT (không chỉ kiểm tra "đã tồn tại thì bỏ qua") —
    /// xác nhận qua source code WgServerForWindows (WS4W, tool quản lý server
    /// WireGuard, github.com/micahmo/WgServerForWindows,
    /// Models/NewNetNatPrerequisite.cs): nút "Enable NAT" của WS4W cũng luôn
    /// Remove-NetNat rồi New-NetNat lại từ đầu mỗi lần, không bao giờ chỉ kiểm
    /// tra rồi bỏ qua. Xác nhận thực nghiệm trên máy dev: `Get-NetNat` báo
    /// Active:True và `Restart-Service WinNat` KHÔNG đủ để NAT hoạt động lại
    /// sau khi tunnel bị start/stop nhiều lần — driver WinNat có thể giữ binding
    /// cũ dù object cấu hình vẫn còn, chỉ xoá hẳn rồi tạo mới mới chắc chắn.
    ///
    /// Tên NAT dùng đúng convention "{tunnel}_nat" (gạch dưới) của WS4W — nếu
    /// tên tunnel trùng (vd "wg_server"), code này thao tác lại ĐÚNG object
    /// WS4W quản lý thay vì tạo thêm 1 object khác tên chồng lên (bug đã gặp).
    /// </summary>
    private static async Task EnsureNatForInterfaceAsync(string interfaceAlias)
    {
        try
        {
            await RunPowerShellAsync(
                $"Set-NetIPInterface -InterfaceAlias '{interfaceAlias}' -Forwarding Enabled -ErrorAction SilentlyContinue");

            // PrefixOrigin=Manual lọc đúng IP tĩnh thật của tunnel (vd 10.253.0.x) —
            // KHÔNG lọc theo "First" đơn thuần, vì interface có thể có thêm 1 địa
            // chỉ APIPA tự động (169.254.0.0/16) nếu bị enumerate trước IP tĩnh,
            // dẫn tới tính sai subnet và tạo nhầm NetNat vô dụng (đã xảy ra thực tế).
            var ipOutput = (await RunPowerShellAsync(
                $"Get-NetIPAddress -InterfaceAlias '{interfaceAlias}' -AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.PrefixOrigin -eq 'Manual' } | " +
                "Select-Object -First 1 | ForEach-Object { \"$($_.IPAddress)|$($_.PrefixLength)\" }")).Trim();

            if (string.IsNullOrEmpty(ipOutput) || !ipOutput.Contains('|')) return;

            var parts = ipOutput.Split('|');
            if (!System.Net.IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefixLen))
                return;

            var networkPrefix = $"{ComputeNetworkAddress(ip, prefixLen)}/{prefixLen}";
            var natName = $"{interfaceAlias}_nat";

            await RunPowerShellAsync($"Remove-NetNat -Name '{natName}' -Confirm:$false -ErrorAction SilentlyContinue");
            await RunPowerShellAsync(
                $"New-NetNat -Name '{natName}' -InternalIPInterfaceAddressPrefix '{networkPrefix}' -ErrorAction SilentlyContinue");
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Đã xoá+tạo lại NAT cho '{interfaceAlias}' ({networkPrefix}).");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[WireGuardConflictGuard] Ensure NAT lỗi: {ex.Message}");
        }
    }

    private static string ComputeNetworkAddress(System.Net.IPAddress ip, int prefixLength)
    {
        var b = ip.GetAddressBytes();
        uint ipUint = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = prefixLength == 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLength);
        uint network = ipUint & mask;
        return new System.Net.IPAddress(new byte[]
        {
            (byte)(network >> 24), (byte)(network >> 16), (byte)(network >> 8), (byte)network
        }).ToString();
    }

    /// <summary>Danh sách tên tunnel "WireGuardTunnel$*" đã cài trên máy (đã bỏ prefix) — dùng cho dropdown chọn ở Dev Panel.</summary>
    public static async Task<List<string>> GetAvailableTunnelNamesAsync()
    {
        try
        {
            var output = await RunPowerShellAsync(
                "Get-Service -Name 'WireGuardTunnel$*' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name");

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => n.Length > 0)
                .Select(n => n.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)
                    ? n["WireGuardTunnel$".Length..]
                    : n)
                .ToList();
        }
        catch
        {
            return new List<string>();
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
