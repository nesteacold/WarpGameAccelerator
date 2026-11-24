// ============================================================
// Services/SessionLogArchive.cs
// Giữ lại log của TỪNG PHIÊN mihomo thay vì huỷ nó mỗi lần bật Boost.
//
// Lý do tồn tại (đo, không cảm nhận): mỗi lần bật Boost, code cũ chạy
// `File.WriteAllText(mihomo_runtime.log, "")` — 294 phiên Boost là 294 lần xoá sạch, chỉ ~0,3% dữ liệu
// mạng sống sót tới lúc đọc. Người dùng gặp sự cố rồi bấm Stop/Start (đúng lời khuyên trong chính thông
// báo lỗi của app) là tự xoá bằng chứng của mình.
//
// Đổi tên thay vì cắt nội dung, vì mihomo có thể vẫn còn giữ handle của file cũ; và đặt tên theo
// LastWriteTime của chính nội dung để "tìm phiên gần thời điểm xảy ra sự cố" làm được bằng một lệnh ls.
// ============================================================
using System.IO;

namespace WarpGameAccelerator.Services;

public static class SessionLogArchive
{
    // Hai trần này là mặc định an toàn, CHƯA đo được dung lượng thật của một phiên Boost dài;
    // khi kho bắt đầu tích luỹ thì nên chốt lại bằng số thật.
    public const int KeepSessions = 20;
    public const long MaxArchiveBytes = 64L * 1024 * 1024;

    public static string? ArchiveAndPrune(string currentLogPath, string archiveDir,
                                          int keepSessions = KeepSessions,
                                          long maxArchiveBytes = MaxArchiveBytes)
    {
        try
        {
            var fi = new FileInfo(currentLogPath);
            if (!fi.Exists || fi.Length == 0) return null;   // đừng nhét file rỗng vào kho

            Directory.CreateDirectory(archiveDir);

            var stamp = fi.LastWriteTime.ToString("yyyyMMdd-HHmmss");
            var dest = Path.Combine(archiveDir, $"mihomo_runtime-{stamp}.log");
            // Hai phiên trong cùng một GIÂY và cùng dung lượng sẽ cho trùng tên; đè lên nhau ở đây
            // nghĩa là mất một phiên log — đúng thứ mà class này sinh ra để chống lại.
            for (int i = 1; File.Exists(dest); i++)
                dest = Path.Combine(archiveDir, $"mihomo_runtime-{stamp}-{i}.log");

            File.Move(currentLogPath, dest);
            Prune(archiveDir, keepSessions, maxArchiveBytes);
            return dest;
        }
        catch { return null; }   // không bao giờ vì việc lưu log mà làm hỏng việc dựng tunnel
    }

    // Giữ các file MỚI NHẤT cho tới khi hết cả hai trần; file nào vượt trần (kể cả vì số lượng) bị xoá.
    public static void Prune(string archiveDir, int keepSessions = KeepSessions,
                             long maxArchiveBytes = MaxArchiveBytes)
    {
        try
        {
            if (!Directory.Exists(archiveDir)) return;

            var files = new DirectoryInfo(archiveDir)
                .GetFiles("mihomo_runtime-*.log")
                // Same-second sessions tie on LastWriteTime; Name breaks the tie so "newest" is
                // deterministic instead of filesystem order.
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();

            long bytesKept = 0;
            int sessionsKept = 0;
            foreach (var f in files)
            {
                bool overCount = sessionsKept >= keepSessions;
                bool overBytes = bytesKept + f.Length > maxArchiveBytes;
                if (overCount || overBytes)
                {
                    try { f.Delete(); } catch { }
                    continue;
                }
                bytesKept += f.Length;
                sessionsKept++;
            }
        }
        catch { }
    }
}
