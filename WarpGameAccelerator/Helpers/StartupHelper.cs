// ============================================================
// Helpers/StartupHelper.cs — Auto-start manager qua Task Scheduler
//
// LỊCH SỬ QUAN TRỌNG (đừng lặp lại):
// Bản cũ dùng Registry Run key (HKCU\...\CurrentVersion\Run). App này
// khai requestedExecutionLevel=requireAdministrator trong app.manifest
// (luôn cần chạy admin) — Windows KHÔNG cho phép entry trong Run key
// tự khởi động app cần admin lúc đăng nhập, vì việc đó cần hiện UAC
// prompt mà Run key không có cơ chế xin silent. Windows âm thầm bỏ
// qua, không launch, không báo lỗi gì — bug "bật auto-start nhưng
// không thấy chạy".
// → Dùng Task Scheduler (`schtasks.exe`) với /RL HIGHEST (Run with
//   highest privileges) — cho phép chạy elevated mà không hiện UAC,
//   vì user đã xác nhận quyền lúc tạo task (chính app đang chạy admin
//   khi gọi EnableAutoStart()).
// ============================================================
using System.Diagnostics;

namespace WarpGameAccelerator.Helpers;

public static class StartupHelper
{
    private const string TaskName = "WarpGameAccelerator_AutoStart";

    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "WarpGameAccelerator";

    public static bool IsAutoStartEnabled()
    {
        var (exitCode, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return exitCode == 0;
    }

    public static void EnableAutoStart()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            RunSchtasks(
                $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" " +
                "/SC ONLOGON /RL HIGHEST /IT");

            RemoveLegacyRunEntry();
        }
        catch { /* log hoặc bỏ qua */ }
    }

    // Chạy lúc đăng nhập là lúc Windows đọc Run key — nếu còn lại entry của bản cũ thì người dùng
    // có HAI đường autostart. Đo trên máy chủ dự án 2026-11-01: task ONLOGON/HIGHEST đang chạy,
    // VÀ Run key `WarpGameAccelerator` vẫn trỏ `Downloads\Warp\WarpGameAccelerator.exe` (file đã
    // không còn ⇒ hiện vô hại). Rủi ro thật: ai đó đặt lại exe vào đường đó là hai phiên bản cùng
    // tự khởi động. Chỉ xoá khi tên AND nội dung đều là của app này — không đụng entry app khác.
    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(LegacyRunKey, writable: true);
            if (run is null) return;
            if (run.GetValue(LegacyRunValue) is not string target) return;
            if (!target.Contains("WarpGameAccelerator", StringComparison.OrdinalIgnoreCase)) return;

            run.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch { /* không được để việc dọn registry làm hỏng thao tác bật auto-start */ }
    }

    public static void DisableAutoStart()
    {
        try
        {
            RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        }
        catch { }
    }

    /// <summary>Trần chờ cho một lời gọi schtasks. Bản khỏe đo được p50 15 ms · max 23 ms, còn vách
    /// "Windows coi là treo" do experiments/hang-lab đo là ~5,1 s ⇒ 5 s bảo đảm đường này không bao
    /// giờ tự tạo ra một cú treo.</summary>
    private const int SchtasksTimeoutMs = 5000;

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "schtasks.exe",
                    Arguments              = arguments,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden
                }
            };

            process.Start();

            // PHẢI rút cả hai luồng ngay: stderr đã mở redirect mà không đọc thì con tre block luc ghi,
            // cha block ở ReadToEnd/WaitForExit ⇒ kẹt vĩnh viễn. Ngưỡng đo được
            // (experiments/redirect-deadlock): stderr 1–4 KB vô hại, ≥8 KB deadlock 100%.
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(SchtasksTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }

            var output = outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();   // rút cho hết pipe; schtasks không có kênh log nào khác
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
