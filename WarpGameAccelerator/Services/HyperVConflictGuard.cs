// ============================================================
// Services/HyperVConflictGuard.cs
// Tạm tắt binding NDIS "Hyper-V Extensible Virtual Switch" (ComponentID
// vms_pp) trên các adapter đang bật binding này — xác định là nguyên nhân
// gốc của "ping timeout + rớt client + giật CRD" khi Boost (xem CLAUDE.md,
// mục "Hyper-V xung đột với TUN"). KHÔNG tắt cả tính năng Hyper-V hay
// hypervisor — chỉ gỡ đúng binding trên adapter, giữ được VM/HVCI.
//
// Tự trả lại đúng adapter đã tắt binding khi Stop Boost, giống pattern
// WireGuardConflictGuard.
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public static class HyperVConflictGuard
{
    private const string ComponentId = "vms_pp";

    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "hyperv_paused_bindings.json");

    /// <summary>True nếu có adapter nào đang Enabled binding vms_pp — dùng để hiển thị "đã phát hiện" trên UI, không tự sửa gì.</summary>
    public static async Task<bool> IsDetectedAsync()
    {
        try
        {
            var names = await GetEnabledAdapterNamesAsync();
            return names.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tắt binding vms_pp trên mọi adapter đang Enabled, ghi nhớ ra file để Resume trả lại đúng adapter.</summary>
    public static async Task PauseAsync()
    {
        try
        {
            var names = await GetEnabledAdapterNamesAsync();
            if (names.Count == 0) return;

            foreach (var name in names)
                await RunPowerShellAsync($"Disable-NetAdapterBinding -Name '{name}' -ComponentID {ComponentId} -Confirm:$false -ErrorAction SilentlyContinue");

            var dir = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(StateFilePath, JsonSerializer.Serialize(names));

            DiagnosticLogService.Trace($"[HyperVConflictGuard] Đã tắt binding {ComponentId} trên: {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[HyperVConflictGuard] Pause lỗi: {ex.Message}");
        }
    }

    /// <summary>Bật lại binding vms_pp trên đúng adapter đã tắt ở PauseAsync.</summary>
    public static async Task ResumeAsync()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;

            var json = await File.ReadAllTextAsync(StateFilePath);
            var names = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

            foreach (var name in names)
                await RunPowerShellAsync($"Enable-NetAdapterBinding -Name '{name}' -ComponentID {ComponentId} -ErrorAction SilentlyContinue");

            File.Delete(StateFilePath);
            DiagnosticLogService.Trace($"[HyperVConflictGuard] Đã bật lại binding {ComponentId} trên: {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[HyperVConflictGuard] Resume lỗi: {ex.Message}");
        }
    }

    private static async Task<List<string>> GetEnabledAdapterNamesAsync()
    {
        var output = await RunPowerShellAsync(
            $"Get-NetAdapterBinding -ComponentID {ComponentId} -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Enabled -eq $true } | Select-Object -ExpandProperty Name");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
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
