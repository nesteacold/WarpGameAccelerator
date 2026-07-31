// ============================================================
// Services/WindowHelper.cs
// Ẩn/hiện cửa sổ client game bằng WinAPI ShowWindow từ process ngoài —
// KHÔNG inject/hook vào fxgame.exe (chỉ thao tác trên HWND, giống hệt
// Task Manager/Alt-Tab làm được), rủi ro bị anti-cheat để ý thấp hơn nhiều
// so với can thiệp vào bộ nhớ/process của game.
//
// Luôn hỏi thẳng Windows (IsWindowVisible) thay vì tự lưu cờ "đang ẩn" nội
// bộ — tránh lệch trạng thái nếu app bị tắt/mở lại giữa chừng trong lúc
// client đang ẩn.
// ============================================================
using System.Runtime.InteropServices;

namespace WarpGameAccelerator.Services;

public static class WindowHelper
{
    private const int SW_HIDE      = 0;
    private const int SW_SHOWNA    = 8; // Show nhưng không cướp focus của cửa sổ khác

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Cache HWND đã tìm được theo PID trong phiên chạy hiện tại — tránh quét
    // lại EnumWindows toàn hệ thống mỗi lần refresh (mỗi 2s cho mỗi client).
    // Vẫn tự validate lại bằng IsWindow trước khi dùng vì client có thể đã
    // đóng cửa sổ cũ và mở cửa sổ mới với cùng PID (hiếm nhưng có thể).
    private static readonly Dictionary<int, IntPtr> _hwndCache = new();

    /// <summary>
    /// Tìm main window thật của 1 PID. fxgame.exe có thể có nhiều top-level
    /// window cùng lúc (ví dụ hộp thoại "lua error" song song với cửa sổ
    /// game) — không thể lấy window đầu tiên tìm thấy trong EnumWindows vì
    /// thứ tự z-order có thể trúng nhầm hộp thoại lỗi. Thay vào đó: bỏ qua
    /// cửa sổ có owner (dialog luôn có owner là cửa sổ cha) rồi chọn cửa sổ
    /// diện tích lớn nhất trong các cửa sổ còn lại — cửa sổ game luôn lớn
    /// hơn hẳn bất kỳ hộp thoại lỗi nào.
    /// </summary>
    public static IntPtr FindMainWindowByPid(int pid)
    {
        if (_hwndCache.TryGetValue(pid, out var cached) && IsWindow(cached))
            return cached;

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != (uint)pid) return true;

            // Dialog (vd: "lua error") luôn có owner trỏ về cửa sổ cha —
            // cửa sổ game chính không có owner.
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

            if (!GetWindowRect(hWnd, out var rect)) return true;
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true; // tiếp tục quét hết, không dừng sớm
        }, IntPtr.Zero);

        if (best != IntPtr.Zero)
            _hwndCache[pid] = best;

        return best;
    }

    public static bool IsClientVisible(int pid)
    {
        var hwnd = FindMainWindowByPid(pid);
        return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    }

    /// <summary>
    /// Mọi top-level window của PID (cửa sổ game + mọi hộp thoại con như
    /// "lua error"). SW_HIDE trên cửa sổ cha KHÔNG tự ẩn owned popup trong
    /// Win32 (khác với minimize) — phải tự enumerate và ẩn từng cái.
    /// </summary>
    private static List<IntPtr> FindAllWindowsForPid(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid) list.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool HideClient(int pid)
    {
        var windows = FindAllWindowsForPid(pid);
        bool any = false;
        foreach (var hWnd in windows)
            any |= ShowWindow(hWnd, SW_HIDE);
        return any;
    }

    public static bool UnhideClient(int pid)
    {
        var windows = FindAllWindowsForPid(pid);
        bool any = false;
        foreach (var hWnd in windows)
            any |= ShowWindow(hWnd, SW_SHOWNA);
        return any;
    }
}
