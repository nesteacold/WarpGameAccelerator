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
    private static readonly string TraceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Logs", "trace.log");

    private static readonly object _lock = new();

    /// <summary>
    /// Ghi 1 dòng trace kèm timestamp + thread id, mở/đóng file ngay mỗi lần
    /// để đảm bảo nội dung đã nằm trên đĩa kể cả khi process bị kill ngay sau đó.
    /// </summary>
    public static void Trace(string message)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(TraceLogPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                File.AppendAllText(TraceLogPath, line);
            }
        }
        catch
        {
            // Fail-safe tuyệt đối — việc ghi log không bao giờ được gây lỗi thêm
        }
    }
}
