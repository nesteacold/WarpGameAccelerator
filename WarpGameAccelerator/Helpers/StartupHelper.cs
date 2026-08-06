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
        }
        catch { /* log hoặc bỏ qua */ }
    }

    public static void DisableAutoStart()
    {
        try
        {
            RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        }
        catch { }
    }

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
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
