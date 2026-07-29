// ============================================================
// Services/NetworkOptimizerService.cs
// Tinh chỉnh TCP để giảm độ trễ khi Boost, và khôi phục khi tắt.
//
// LỊCH SỬ QUAN TRỌNG (đừng lặp lại):
// Bản cũ còn đổi MTU của MỌI card mạng vật lý xuống 1420 bằng
// `netsh ... store=persistent`. Việc đó VÔ TÁC DỤNG với game (gói tin
// game đã bị TUN adapter giới hạn ở 1280 từ trước), lại gây mất mạng
// vài giây mỗi lần bật/tắt, và vì backup chỉ nằm trong RAM nên khi app
// bị kill là MTU 1420 kẹt lại VĨNH VIỄN trên máy người dùng.
// → Đã bỏ hẳn phần MTU. Xem CleanupLegacyMtuAsync() để dọn cho những
//   máy đã lỡ dính bản cũ.
//
// Backup nay ghi ra FILE (không phải RAM) để nếu app bị kill giữa chừng
// thì lần khởi động sau vẫn khôi phục được.
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WarpGameAccelerator.Services;

public class NetworkBackupState
{
    /// <summary>Giá trị TcpAckFrequency/TCPNoDelay gốc theo từng interface GUID.</summary>
    public Dictionary<string, TcpInterfaceBackup> Interfaces { get; set; } = new();

    /// <summary>Đã dọn xong MTU 1420 do bản cũ để lại hay chưa (chỉ chạy 1 lần).</summary>
    public bool LegacyMtuCleaned { get; set; }

    public string SavedAt { get; set; } = string.Empty;
}

public class TcpInterfaceBackup
{
    public int? TcpAckFrequency { get; set; }
    public int? TcpNoDelay      { get; set; }
}

public class NetworkOptimizerService
{
    private const string TcpipInterfacesKey =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>MTU mà bản cũ đặt — dùng để nhận diện máy cần dọn dẹp.</summary>
    private const int LegacyMtuValue   = 1420;
    private const int WindowsDefaultMtu = 1500;

    private static readonly string BackupFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "network_backup.json");

    // ── API chính ────────────────────────────────────────────

    public async Task OptimizeAsync()
    {
        await Task.Run(BackupAndOptimizeRegistry);
    }

    public async Task RestoreAsync()
    {
        await Task.Run(RestoreRegistryFromBackup);
    }

    /// <summary>
    /// Gọi lúc app khởi động. Làm 2 việc:
    ///  1. Nếu phiên trước bị kill giữa chừng (còn backup chưa khôi phục) →
    ///     khôi phục ngay.
    ///  2. Dọn MTU 1420 do bản cũ để lại (chỉ chạy một lần duy nhất).
    /// </summary>
    public async Task RecoverPendingChangesAsync()
    {
        try
        {
            var state = LoadState();

            if (state.Interfaces.Count > 0)
            {
                DiagnosticLogService.Trace(
                    $"NetworkOptimizer: phát hiện {state.Interfaces.Count} interface chưa khôi phục từ phiên trước");
                await Task.Run(RestoreRegistryFromBackup);
            }

            if (!state.LegacyMtuCleaned)
            {
                await CleanupLegacyMtuAsync();
                state = LoadState();
                state.LegacyMtuCleaned = true;
                SaveState(state);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NetworkOptimizer.RecoverPendingChanges lỗi: {ex.Message}");
        }
    }

    // ── Registry TCP (TcpAckFrequency / TCPNoDelay) ──────────

    private void BackupAndOptimizeRegistry()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesKey, writable: true);
            if (baseKey == null) return;

            var state = LoadState();

            foreach (var interfaceName in baseKey.GetSubKeyNames())
            {
                using var interfaceKey = baseKey.OpenSubKey(interfaceName, writable: true);
                if (interfaceKey == null) continue;

                // Không ghi đè backup cũ nếu đã có (tránh backup lại chính giá
                // trị mình vừa đặt khi Optimize được gọi 2 lần liên tiếp).
                if (!state.Interfaces.ContainsKey(interfaceName))
                {
                    state.Interfaces[interfaceName] = new TcpInterfaceBackup
                    {
                        TcpAckFrequency = interfaceKey.GetValue("TcpAckFrequency") as int?,
                        TcpNoDelay      = interfaceKey.GetValue("TCPNoDelay") as int?
                    };
                }

                interfaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                interfaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
            }

            SaveState(state);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NetworkOptimizer.Optimize lỗi: {ex.Message}");
        }
    }

    private void RestoreRegistryFromBackup()
    {
        try
        {
            var state = LoadState();
            if (state.Interfaces.Count == 0) return;

            using var baseKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesKey, writable: true);
            if (baseKey == null) return;

            foreach (var (interfaceName, backup) in state.Interfaces)
            {
                using var interfaceKey = baseKey.OpenSubKey(interfaceName, writable: true);
                if (interfaceKey == null) continue;

                if (backup.TcpAckFrequency.HasValue)
                    interfaceKey.SetValue("TcpAckFrequency", backup.TcpAckFrequency.Value, RegistryValueKind.DWord);
                else
                    interfaceKey.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);

                if (backup.TcpNoDelay.HasValue)
                    interfaceKey.SetValue("TCPNoDelay", backup.TcpNoDelay.Value, RegistryValueKind.DWord);
                else
                    interfaceKey.DeleteValue("TCPNoDelay", throwOnMissingValue: false);
            }

            // Xoá backup nhưng GIỮ cờ LegacyMtuCleaned
            state.Interfaces.Clear();
            SaveState(state);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NetworkOptimizer.Restore lỗi: {ex.Message}");
        }
    }

    // ── Dọn dẹp MTU 1420 do bản cũ để lại (chạy 1 lần) ───────

    private async Task CleanupLegacyMtuAsync()
    {
        try
        {
            var output = await RunNetshAsync("interface ipv4 show subinterfaces");
            if (string.IsNullOrWhiteSpace(output)) return;

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[0], out int mtu)) continue;

                // Chỉ đụng vào interface đang ở đúng 1420 — giá trị mà bản cũ
                // đặt. Không đoán mò với các giá trị khác (1492 PPPoE, v.v.)
                if (mtu != LegacyMtuValue) continue;

                var name = string.Join(" ", parts, 4, parts.Length - 4);
                if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Pseudo", StringComparison.OrdinalIgnoreCase))
                    continue;

                DiagnosticLogService.Trace($"NetworkOptimizer: trả MTU '{name}' 1420 → {WindowsDefaultMtu}");
                await RunNetshAsync(
                    $"interface ipv4 set subinterface \"{name}\" mtu={WindowsDefaultMtu} store=persistent");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NetworkOptimizer.CleanupLegacyMtu lỗi: {ex.Message}");
        }
    }

    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "netsh",
                    Arguments              = arguments,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden
                }
            };

            process.Start();

            // Đọc stdout TRƯỚC rồi mới WaitForExit — đọc trong sự kiện Exited
            // có thể deadlock khi buffer đầy.
            var readTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return await readTask;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"netsh '{arguments}' lỗi: {ex.Message}");
            return string.Empty;
        }
    }

    // ── Lưu/đọc backup ra file ───────────────────────────────

    private static NetworkBackupState LoadState()
    {
        try
        {
            if (!File.Exists(BackupFilePath)) return new NetworkBackupState();
            var json = File.ReadAllText(BackupFilePath);
            return JsonSerializer.Deserialize<NetworkBackupState>(json) ?? new NetworkBackupState();
        }
        catch
        {
            return new NetworkBackupState();
        }
    }

    private static void SaveState(NetworkBackupState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(BackupFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            state.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.WriteAllText(BackupFilePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"NetworkOptimizer.SaveState lỗi: {ex.Message}");
        }
    }
}
