// ============================================================
// Services/NvOverlayOptimizerService.cs
// Tắt NvContainerLocalSystem (NVIDIA Share/GeForce Experience overlay) +
// Windows Game DVR khi bật, khôi phục khi tắt — đo được giảm ~32% GPU,
// ~19.5% RAM, ~11% CPU mỗi client AOW (nguồn: phiên làm việc DXVK wrapper).
//
// DXVK wrapper chạy trong fxgame.exe (không elevated) nên không tự Stop
// Service được — WarpGameAccelerator đã requireAdministrator sẵn nên đảm
// nhiệm việc này thay vì cần thêm 1 thành phần Admin riêng.
//
// Cố tình KHÔNG tự phục hồi khi hết client AOW (đã cân nhắc, bỏ để giảm
// độ phức tạp) — chỉ áp dụng/khôi phục theo đúng lúc người dùng bật/tắt
// toggle, hoặc lúc app khởi động nếu phiên trước bị crash dở dang (giống
// hệt pattern NetworkOptimizerService.cs). Đây là toggle TOÀN MÁY, ảnh
// hưởng mọi app dùng GeForce Experience overlay/ShadowPlay trong lúc bật,
// không chỉ riêng AOW.
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WarpGameAccelerator.Services;

public class NvOverlayBackupState
{
    /// <summary>StartType gốc của NvContainerLocalSystem trước khi Optimize (vd "Automatic", "Manual", "Disabled").</summary>
    public string? ServiceOriginalStartType { get; set; }

    /// <summary>Giá trị gốc của GameDVR_Enabled — null nghĩa là key chưa từng tồn tại.</summary>
    public int? GameDvrOriginalValue { get; set; }

    public bool Applied { get; set; }
}

public static class NvOverlayOptimizerService
{
    private const string ServiceName = "NvContainerLocalSystem";
    private const string GameDvrKey  = @"System\GameConfigStore";
    private const string GameDvrValueName = "GameDVR_Enabled";

    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "nv_overlay_backup.json");

    public static bool IsApplied() => LoadState().Applied;

    /// <summary>Tắt NvContainerLocalSystem + Game DVR, lưu giá trị gốc trước khi đổi.</summary>
    public static async Task ApplyAsync()
    {
        try
        {
            var state = LoadState();
            if (state.Applied) return; // đã áp dụng rồi, tránh backup đè lên chính giá trị mình vừa đặt

            state.ServiceOriginalStartType = await GetServiceStartTypeAsync();

            using (var key = Registry.CurrentUser.OpenSubKey(GameDvrKey, writable: true)
                              ?? Registry.CurrentUser.CreateSubKey(GameDvrKey))
            {
                state.GameDvrOriginalValue = key?.GetValue(GameDvrValueName) as int?;
                key?.SetValue(GameDvrValueName, 0, RegistryValueKind.DWord);
            }

            await RunPowerShellAsync($"Set-Service -Name '{ServiceName}' -StartupType Disabled -ErrorAction SilentlyContinue");
            await RunPowerShellAsync($"Stop-Service -Name '{ServiceName}' -Force -ErrorAction SilentlyContinue");

            state.Applied = true;
            SaveState(state);
            DiagnosticLogService.Trace("NvOverlayOptimizer: đã tắt NvContainerLocalSystem + Game DVR.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NvOverlayOptimizer.Apply lỗi: {ex.Message}");
        }
    }

    /// <summary>Khôi phục NvContainerLocalSystem + Game DVR về giá trị gốc đã backup.</summary>
    public static async Task RestoreAsync()
    {
        try
        {
            var state = LoadState();
            if (!state.Applied) return;

            using (var key = Registry.CurrentUser.OpenSubKey(GameDvrKey, writable: true))
            {
                if (state.GameDvrOriginalValue.HasValue)
                    key?.SetValue(GameDvrValueName, state.GameDvrOriginalValue.Value, RegistryValueKind.DWord);
                else
                    key?.DeleteValue(GameDvrValueName, throwOnMissingValue: false);
            }

            var startType = string.IsNullOrEmpty(state.ServiceOriginalStartType) ? "Manual" : state.ServiceOriginalStartType;
            await RunPowerShellAsync($"Set-Service -Name '{ServiceName}' -StartupType {startType} -ErrorAction SilentlyContinue");
            await RunPowerShellAsync($"Start-Service -Name '{ServiceName}' -ErrorAction SilentlyContinue");

            state.Applied = false;
            state.ServiceOriginalStartType = null;
            state.GameDvrOriginalValue = null;
            SaveState(state);
            DiagnosticLogService.Trace("NvOverlayOptimizer: đã khôi phục NvContainerLocalSystem + Game DVR.");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NvOverlayOptimizer.Restore lỗi: {ex.Message}");
        }
    }

    /// <summary>Gọi lúc app khởi động — nếu phiên trước bị crash lúc đang Applied, tự khôi phục ngay.</summary>
    public static async Task RecoverPendingChangesAsync()
    {
        var state = LoadState();
        if (!state.Applied) return;

        DiagnosticLogService.Trace("NvOverlayOptimizer: phát hiện phiên trước chưa khôi phục — tự khôi phục ngay.");
        await RestoreAsync();
    }

    private static async Task<string> GetServiceStartTypeAsync()
    {
        var output = await RunPowerShellAsync(
            $"(Get-Service -Name '{ServiceName}' -ErrorAction SilentlyContinue).StartType");
        var startType = output.Trim();
        return string.IsNullOrEmpty(startType) ? "Manual" : startType;
    }

    private static async Task<string> RunPowerShellAsync(string command)
    {
        try
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
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NvOverlayOptimizer PowerShell lỗi: {ex.Message}");
            return string.Empty;
        }
    }

    private static NvOverlayBackupState LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return new NvOverlayBackupState();
            var json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<NvOverlayBackupState>(json) ?? new NvOverlayBackupState();
        }
        catch
        {
            return new NvOverlayBackupState();
        }
    }

    private static void SaveState(NvOverlayBackupState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NvOverlayOptimizer.SaveState lỗi: {ex.Message}");
        }
    }
}
