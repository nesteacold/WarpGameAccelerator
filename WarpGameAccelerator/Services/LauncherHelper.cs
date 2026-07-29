// ============================================================
// Services/LauncherHelper.cs
// "Tiến trình cha thế mạng" cho fxgame.exe.
//
// VÌ SAO CẦN: fxgame.exe khi khởi động xong sẽ GIẾT TIẾN TRÌNH CHA
// của nó — đây là cơ chế bình thường để game tự đóng launcher
// (fxlaunch.exe) sau khi đã lên. Nếu app chính tự gọi
// Process.Start(fxgame.exe) thì app chính trở thành cha và bị giết
// theo đúng cơ chế đó (đã xác minh qua Event Log
// Microsoft-Windows-ProcessExitMonitor Id 3001).
//
// GIẢI PHÁP: chèn một tiến trình trung gian đứng vào vai trò cha.
// Nó bị giết cũng không sao — không có UI, không giữ state gì.
// Tiến trình này chính là file .exe của app chạy với tham số
// --launch-game (xem Program.cs), nên không cần binary riêng.
// ============================================================
using System.Diagnostics;
using System.IO;

namespace WarpGameAccelerator.Services;

public static class LauncherHelper
{
    public const string HelperArgument = "--launch-game";

    /// <summary>
    /// Thời gian helper đứng giữ vai trò cha. Thực tế game giết helper chỉ
    /// sau vài chục giây; timeout này chỉ để helper không sống mãi nếu vì
    /// lý do nào đó game không giết nó.
    /// </summary>
    private const int HelperLifetimeMs = 90_000;

    /// <summary>
    /// Chạy trong tiến trình HELPER: mở game rồi đứng chờ để làm cha.
    /// Không khởi tạo UI, DI hay Mihomo — xem Program.cs.
    /// </summary>
    public static void RunAsGameParent(string gamePath, string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath)) return;

            var workDir = Path.GetDirectoryName(gamePath) ?? string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName         = gamePath,
                Arguments        = token,
                WorkingDirectory = workDir,
                // PHẢI dùng UseShellExecute = true, đúng như cách bản cũ khởi
                // chạy game. Với false, game được tạo bằng CreateProcess và kế
                // thừa môi trường/handle của helper (vốn chạy ẩn, không cửa sổ)
                // → client vào game bị báo mất kết nối một lúc mới ổn định.
                // Dùng true vẫn giữ nguyên quan hệ cha–con nên helper vẫn làm
                // đúng vai trò hứng đòn thay app chính.
                UseShellExecute  = true
            };

            using var game = Process.Start(psi);
            if (game == null) return;

            // Đứng chờ để giữ quan hệ cha–con. Game sẽ giết tiến trình này;
            // nếu không, tự thoát sau HelperLifetimeMs.
            game.WaitForExit(HelperLifetimeMs);
        }
        catch
        {
            // Helper phải im lặng tuyệt đối — nó không có UI để báo lỗi, và
            // app chính đã tự xác minh kết quả bằng cách đếm tiến trình game.
        }
    }

    /// <summary>
    /// Gọi từ APP CHÍNH: mở 1 cửa sổ game thông qua helper, để helper (chứ
    /// không phải app) đứng vào vai trò tiến trình cha bị game giết.
    /// </summary>
    public static void LaunchGameViaHelper(string gamePath, string token)
    {
        var selfExe = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrEmpty(selfExe))
            throw new InvalidOperationException("Không xác định được đường dẫn ứng dụng để tạo helper.");

        var psi = new ProcessStartInfo
        {
            FileName        = selfExe,
            UseShellExecute = false,
            CreateNoWindow  = true,
            WindowStyle     = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add(HelperArgument);
        psi.ArgumentList.Add(gamePath);
        psi.ArgumentList.Add(token);

        using var helper = Process.Start(psi);
        DiagnosticLogService.Trace($"  đã spawn helper PID={helper?.Id.ToString() ?? "?"}");
    }
}
