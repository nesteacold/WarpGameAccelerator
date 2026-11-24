// ============================================================
// Services/SingleInstanceGuard.cs
// Chặn mở 2 instance app chính cùng lúc.
//
// VÌ SAO CẦN: mỗi instance khi khởi động đều gọi StopProxy() (trong
// ExtractCoreResources()) để dọn mihomo cũ trước khi extract lại —
// mở instance thứ 2 sẽ kill mihomo của instance thứ 1 đang phục vụ
// game, làm rớt toàn bộ client mà không có cảnh báo gì. Dấu hiệu
// từng gặp trong log: "RegisterHotKey thất bại — tổ hợp phím có thể
// đã bị chiếm".
//
// Named Mutex không áp dụng cho tiến trình helper (--launch-game,
// xem LauncherHelper) — helper không khởi tạo DI/Mihomo nên không
// xung đột, và phải được phép chạy song song với app chính.
// ============================================================
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace WarpGameAccelerator.Services;

public static class SingleInstanceGuard
{
    private const string MutexName = "Global\\WarpGameAccelerator_SingleInstance";
    private static Mutex? _held;

    // Instance cũ giữ khoá SUỐT cuộc dọn dẹp của lệnh thoát (StopProxy → warp-cli → conflict
    // restore, trần 3 s ở MainWindow.ExitApp). Mở lại trong lúc đó là KHÔNG hợp lệ — tunnel mà
    // instance mới vừa dựng sẽ bị cuộc dọn dẹp kia tắt — nhưng nó là trạng thái TẠM, nên phải
    // chờ một chút chứ không được từ chối ngay (chính cái từ-chối-ngay đã đẻ ra hộp thoại
    // "đang chạy rồi" treo trên màn hình ngày 02-11). 4 lần × 400 ms = 1,2 s: phủ được phần
    // lớn cuộc dọn dẹp thật, đổi lại một người bấm mở khi app THỰC sự đang chạy phải chờ 1,2 s
    // trước khi thấy hộp thoại — đánh đổi có ý thức, không phải mặc định.
    private const int AcquireAttempts = 4;
    private const int AcquireRetryDelayMs = 400;

    /// <summary>
    /// Thử giành mutex toàn hệ thống, có thử lại. Trả về true nếu instance này là
    /// instance đầu tiên (được phép chạy tiếp). Nếu false, mutex vẫn bị một tiến trình
    /// khác giữ — gọi <see cref="ShowAlreadyRunningMessage"/> rồi thoát ngay, KHÔNG khởi
    /// tạo App/DI/MihomoService.
    /// </summary>
    public static bool TryAcquire()
    {
        for (int attempt = 1; attempt <= AcquireAttempts; attempt++)
        {
            var candidate = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
            if (createdNew)
            {
                _held = candidate;
                return true;
            }
            candidate.Dispose();
            if (attempt < AcquireAttempts) Thread.Sleep(AcquireRetryDelayMs);
        }
        return false;
    }

    /// <summary>
    /// Nhả quyền sở hữu. Gọi ở CUỐI đường thoát — sau cuộc dọn dẹp (~3 s: StopProxy,
    /// warp-cli Disconnect, ConflictDetectionService.RestoreAll) — vì suốt cuộc đó instance
    /// cũ VẪN đang cắt đường hầm hệ thống; một instance mới mở xen vào sẽ dựng tunnel rồi bị
    /// chính cuộc dọn dẹp kia tắt. Bản trước nhả ở ĐẦU ExitApp và tự tay mở ra đúng race đó.
    /// Idempotent: gọi nhiều lần không ném.
    /// </summary>
    public static void Release()
    {
        var m = _held;
        _held = null;
        if (m == null) return;
        try { m.ReleaseMutex(); } catch { }
        try { m.Dispose(); } catch { }
    }

    /// <summary>
    /// Mô tả ai đang giữ lock, để hộp thoại không còn vô ích. Không có API nào hỏi mutex
    /// "ai đang sở hữu", nên đây là khớp theo TÊN tiến trình (loại chính mình) — đủ để
    /// người dùng biết PID nào cần tắt, và đủ để nói "không thấy instance nào" khi cuộc dọn
    /// dẹp của instance cũ chưa xong mà tiến trình đã tắt.
    /// </summary>
    public static string DescribeOtherInstances()
    {
        try
        {
            var self = Process.GetCurrentProcess().Id;
            var all = Process.GetProcessesByName("WarpGameAccelerator");
            try
            {
                var others = all.Where(p => p.Id != self).ToArray();
                if (others.Length == 0)
                    return "Không tìm thấy tiến trình WarpGameAccelerator nào khác — có thể instance cũ vừa tắt xong.\nBấm Thử lại để mở.";
                var lines = others.Select(p =>
                {
                    string start = "?";
                    try { start = p.StartTime.ToString("dd-MM HH:mm:ss"); } catch { }
                    string resp = p.Responding ? "còn phản hồi" : "KHÔNG phản hồi (treo)";
                    return $"  PID {p.Id}  mở lúc {start}  {resp}";
                }).ToList();
                return "Tiến trình WarpGameAccelerator đang chạy (khớp theo TÊN — không có API nào hỏi mutex "
                       + "ai đang giữ, nên đây là giả nghi chứ không phải lời khẳng định):\n"
                       + string.Join("\n", lines)
                       + "\n\nNếu có một cái KHÔNG phản hồi, đó nhiều khả năng là thủ phạm: tắt nó "
                         + "(Task Manager → Details → End task theo PID) rồi bấm Thử lại.";
            }
            finally
            {
                foreach (var p in all) { try { p.Dispose(); } catch { } }
            }
        }
        catch { return "(không đọc được danh sách tiến trình)"; }
    }

    /// <summary>
    /// True nếu lock đã trống ở thời điểm gọi — dùng để hộp thoại nói ĐÚNG nguyên nhân:
    /// "instance cũ đang tắt dở" khi nó còn sống, "lock đã trống" khi không còn ai.
    /// Chỉ ĐỌC, không giành: Mutex(initiallyOwned: false) mở một handle rồi đóng ngay.
    /// </summary>
    public static bool IsLockFree()
    {
        var candidate = new Mutex(initiallyOwned: false, name: MutexName, createdNew: out var createdNew);
        candidate.Dispose();
        return createdNew;   // createdNew == true nghĩa là chưa tồn tại object mutex nào khác
    }

    /// <summary>
    /// Hộp thoại "đã chạy rồi". Trả về true nếu người dùng bấm Thử lại VÀ lần đó giành được
    /// lock — caller (Program.Main) đi tiếp như instance bình thường; trả về false thì thoát.
    /// Nút Thử lại tồn tại vì lý do đo được: hộp thoại vẫn treo trên màn hình sau khi điều kiện
    /// gây ra nó đã hết (02-11: PID 30396 ở lại trong khi lock đã trống), và cách "mở lại" duy
    /// nhất trước đó là bấm đúp vào .exe — mỗi lần bấm đẻ thêm một hộp thoại cũ chồng lên nhau.
    /// </summary>
    public static bool ShowAlreadyRunningMessage()
    {
        const int MB_ICONWARNING = 0x00000030;
        const int MB_RETRYCANCEL = 0x00000004;   // Retry là nút mặc định
        const int IDRETRY = 4;
        while (true)
        {
            string why = IsLockFree()
                ? "Khoá đã được nhả trong lúc chờ — instance cũ có thể đang ở những giây cuối của lệnh tắt."
                : DescribeOtherInstances();
            int r = MessageBoxW(IntPtr.Zero,
                "WarpGameAccelerator chưa mở được.\n\n"
                + "Không cần tắt thủ công: instance đang giữ khoá sẽ tự nhả khi nó tắt xong.\n\n"
                + why + "\n\nBấm Thử lại để kiểm tra lại ngay (không mở hai cửa sổ cùng lúc).",
                "WarpGameAccelerator",
                MB_ICONWARNING | MB_RETRYCANCEL);
            if (r != IDRETRY) return false;
            if (TryAcquire()) return true;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
