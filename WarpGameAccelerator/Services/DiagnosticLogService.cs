// ============================================================
// Services/DiagnosticLogService.cs
// Ghi trace từng bước ra file, flush ngay lập tức — dùng để xác
// định app chết ở bước nào khi process bị kết thúc đột ngột mà
// không kịp chạy bất kỳ handler exception nào (silent exit).
// ============================================================
using System.IO;

namespace WarpGameAccelerator.Services;

public static class DiagnosticLogService
{
    // Phòng thí nghiệm (experiments/**) biên dịch CHÍNH file này và gọi Trace thật. Không có đường
    // ghi đè thì mỗi lượt lab viết thẳng vào telemetry của bản gửi cho người dùng — đo 2026-11-01:
    // 67 dòng "[HangWatchdog] Treo được xác nhận" do lab tạo nằm trong trace.log thật, và vì lab vừa
    // ghi nên chúng chiếm đúng cửa sổ "phần mới nhất": 98% bằng chứng của báo cáo category
    // Disconnect/Update/Khác là của lab chứ không phải của app. WARP_DIAG_LOG chỉ đổi NƠI ghi;
    // bản gửi không đặt biến này nên đường production giữ nguyên.
    private static readonly string TraceLogPath = ResolveTraceLogPath();

    /// <summary>Nơi bản ghi trace đang nằm — để các bộ đếm/đọc (HangWatchdog, reporter) dùng đúng
    /// đường dẫn đã tôn trọng WARP_DIAG_LOG, thay vì tự lắp lại công thức (lắp lại là lệch nhau).</summary>
    internal static string LogPath => TraceLogPath;

    private static string ResolveTraceLogPath()
    {
        var redirect = Environment.GetEnvironmentVariable("WARP_DIAG_LOG");
        if (!string.IsNullOrWhiteSpace(redirect)) return redirect;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WarpGameAccelerator", "Logs", "trace.log");
    }

    private static readonly object _lock = new();

    // Windows cấp lại PID sau một thời gian, nên PID một mình KHÔNG phân biệt
    // được hai phiên. Session tag làm cho việc đếm phiên/correlate với Event
    // Log chính xác tuyệt đối; PID vẫn giữ vì các log khác của OS gọi tiến
    // trình bằng PID.
    private static readonly string SessionTag = Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// Ghi 1 dòng trace kèm timestamp (có NGÀY) + PID + session + thread id,
    /// mở/đóng file ngay mỗi lần để đảm bảo nội dung đã nằm trên đĩa kể cả khi
    /// process bị kill ngay sau đó.
    /// </summary>
    public static void Trace(string message)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(TraceLogPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [P{Environment.ProcessId} S{SessionTag} T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                File.AppendAllText(TraceLogPath, line);
            }
        }
        catch
        {
            // Fail-safe tuyệt đối — việc ghi log không bao giờ được gây lỗi thêm
        }
    }
}
