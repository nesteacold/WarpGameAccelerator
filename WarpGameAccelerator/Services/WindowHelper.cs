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
//
// ShowWindow là lệnh cross-process và CHẶN cho tới khi thread của cửa sổ
// đích xử lý xong message. Game treo/bận => caller treo theo. Đã đo trong
// experiments/hang-lab: cửa sổ khoẻ 7ms, cửa sổ không pump message >12s
// không trả; SetWindowPos(SWP_HIDEWINDOW) cũng chặn y hệt. Vì vậy MỌI lời gọi
// ShowWindow ở file này phải chạy ngoài UI thread — đi qua RequestHide /
// RequestUnhide, đừng gọi thẳng HideClient/UnhideClient từ event handler hay
// DispatcherTimer.
// ============================================================
using System.Runtime.InteropServices;
using System.Threading;

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

    // Bảo vệ _hwndCache / _hiddenByUs / _inFlight: các worker thread show/hide
    // và UI thread (lúc poll trạng thái) cùng chạm vào chúng.
    private static readonly object _gate = new();

    // PID đang có một lượt show/hide dở. Không được xếp hàng thêm lượt thứ hai
    // cho cùng PID: nếu client treo, mỗi tick 2s sẽ spawn thêm một thread chết
    // đuối vô ích.
    private static readonly HashSet<int> _inFlight = new();

    /// <summary>
    /// Ẩn client trên một thread nền riêng — UI thread không bao giờ chờ
    /// WinAPI. Trả về false nếu lượt trước của PID này chưa xong (bỏ qua, tick
    /// sau sẽ re-assert lại).
    /// </summary>
    public static bool RequestHide(int pid) => Request(pid, hide: true);

    public static bool RequestUnhide(int pid) => Request(pid, hide: false);

    private static bool Request(int pid, bool hide)
    {
        lock (_gate)
        {
            if (!_inFlight.Add(pid)) return false;
        }

        // Dấu vết cho HangWatchdog: đây là họ thao tác từng làm treo cả app (ShowWindow chặn
        // caller). Nếu UI kẹt ngay sau đó, hồ sơ sẽ ghi đúng "hide/unhide pid=… @ WindowHelper.cs:…".
        HangWatchdog.Mark($"show/hide client pid={pid} hide={hide}");

        var worker = new Thread(() =>
        {
            try
            {
                if (hide) HideClient(pid);
                else UnhideClient(pid);
            }
            finally
            {
                lock (_gate) _inFlight.Remove(pid);
            }
        })
        {
            IsBackground = true,
            Name = $"WindowHelper.{(hide ? "hide" : "unhide")}-{pid}"
        };
        worker.Start();
        return true;
    }

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
        lock (_gate)
        {
            if (_hwndCache.TryGetValue(pid, out var cached) && IsWindow(cached))
                return cached;
        }

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
        {
            lock (_gate) _hwndCache[pid] = best;
        }

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

    // Nhớ đúng những HWND MÀ MÌNH đã tự tay ẩn theo từng PID — để Unhide chỉ
    // khôi phục lại đúng những cái đó. Không được unhide "mọi cửa sổ của PID"
    // một cách mù quáng: game có những cửa sổ nội bộ tự nó giữ ẩn từ đầu
    // (vd: "GDI+ Window" — helper window của GDI+, chưa bao giờ hiện ra
    // ngoài taskbar) — nếu vô tình ShowWindow lên chúng sẽ làm lộ ra những
    // thứ mà bản thân game chưa từng định hiện.
    private static readonly Dictionary<int, HashSet<IntPtr>> _hiddenByUs = new();

    /// <summary>
    /// Ẩn mọi cửa sổ ĐANG HIỆN của PID (không đụng cửa sổ vốn đã ẩn sẵn),
    /// ghi nhớ lại chính xác cái nào mình vừa ẩn. Gọi lại nhiều lần (mỗi lần
    /// refresh) trong lúc client đang ở chế độ "ẩn" để dập những cửa sổ hệ
    /// thống bị Windows tự hiện lại giữa chừng (vd: "Default IME" theo focus
    /// bàn phím) — mỗi lần gọi chỉ ẩn thêm cái MỚI xuất hiện, không lặp lại
    /// việc với cái đã ẩn từ trước.
    /// </summary>
    public static bool HideClient(int pid)
    {
        bool any = false;
        foreach (var hWnd in FindAllWindowsForPid(pid))
        {
            if (!IsWindowVisible(hWnd)) continue; // đã ẩn sẵn (do mình hoặc do chính game) — bỏ qua
            any |= ShowWindow(hWnd, SW_HIDE);
            lock (_gate)
            {
                if (!_hiddenByUs.TryGetValue(pid, out var set))
                    _hiddenByUs[pid] = set = new HashSet<IntPtr>();
                set.Add(hWnd);
            }
        }
        return any;
    }

    /// <summary>
    /// Chỉ hiện lại đúng những HWND mà <see cref="HideClient"/> đã từng ẩn
    /// cho PID này — KHÔNG enumerate/show lại toàn bộ cửa sổ của PID.
    /// </summary>
    public static bool UnhideClient(int pid)
    {
        List<IntPtr> set;
        lock (_gate)
        {
            if (!_hiddenByUs.TryGetValue(pid, out var existing) || existing.Count == 0) return false;
            set = existing.ToList();
            _hiddenByUs.Remove(pid);
        }

        bool any = false;
        foreach (var hWnd in set)
        {
            if (!IsWindow(hWnd)) continue; // cửa sổ đã bị đóng từ khi ẩn tới giờ
            any |= ShowWindow(hWnd, SW_SHOWNA);
        }
        return any;
    }

    /// <summary>Dọn state nội bộ khi client đã đóng hẳn (tránh leak dần).</summary>
    public static void ForgetPid(int pid)
    {
        lock (_gate)
        {
            _hiddenByUs.Remove(pid);
            _hwndCache.Remove(pid);
        }
    }
}
