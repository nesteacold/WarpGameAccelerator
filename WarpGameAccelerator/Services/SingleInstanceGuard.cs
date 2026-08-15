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

    /// <summary>
    /// Thử giành quyền sở hữu mutex toàn hệ thống. Trả về true nếu instance
    /// này là instance đầu tiên (được phép chạy tiếp). Nếu false, instance
    /// đã tồn tại — gọi ShowAlreadyRunningMessage() rồi thoát ngay, KHÔNG
    /// khởi tạo App/DI/MihomoService.
    /// </summary>
    public static bool TryAcquire(out Mutex? mutex)
    {
        mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        if (createdNew) return true;

        mutex.Dispose();
        mutex = null;
        return false;
    }

    public static void ShowAlreadyRunningMessage()
    {
        MessageBoxW(IntPtr.Zero,
            "WarpGameAccelerator đang chạy rồi.\n\nMở thêm cửa sổ sẽ làm tắt tunnel của phiên đang chạy và rớt kết nối game. Vui lòng dùng cửa sổ đang mở (kiểm tra khay hệ thống nếu không thấy).",
            "WarpGameAccelerator",
            0x00000030 /* MB_ICONWARNING */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
