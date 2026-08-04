// ============================================================
// Helpers/GlobalHotkeyHelper.cs — Global hotkey via P/Invoke RegisterHotKey
// Dùng hidden message-only window + thread riêng, giống pattern đã kiểm
// chứng ở TrayIconHelper.cs — KHÔNG subclass WndProc của MainWindow (rủi ro
// cao hơn, và RegisterHotKey không cần là window chính để nhận WM_HOTKEY).
// ============================================================
using System.Runtime.InteropServices;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Helpers;

/// <summary>
/// Đăng ký 1 tổ hợp phím tắt toàn cục (global hotkey), gọi callback khi bấm.
/// Dispose() để hủy đăng ký + đóng thread khi không cần nữa.
/// </summary>
public sealed class GlobalHotkeyHelper : IDisposable
{
    private const int WM_HOTKEY  = 0x0312;
    private const int WM_DESTROY = 0x0002;
    private const int HOTKEY_ID  = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int  ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint    cbSize;
        public uint    style;
        public nint    lpfnWndProc;
        public int     cbClsExtra, cbWndExtra;
        public nint    hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public nint    hIconSm;
    }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    // Modifier flags cho RegisterHotKey
    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;

    private nint _hWnd;
    private readonly Thread _msgThread;
    private readonly uint _modifiers;
    private readonly uint _vk;
    private readonly Action _onHotkeyPressed;
    private readonly ManualResetEventSlim _windowReady = new(false);
    private WndProcDelegate? _wndProcDelegate; // Giữ tham chiếu sống — GC sẽ collect delegate nếu không giữ lại
    private bool _disposed;
    private bool _registerFailed;

    /// <param name="modifiers">Kết hợp MOD_ALT | MOD_CONTROL | MOD_SHIFT</param>
    /// <param name="virtualKey">Virtual-key code, ví dụ 0x44 cho 'D'</param>
    public GlobalHotkeyHelper(uint modifiers, uint virtualKey, Action onHotkeyPressed)
    {
        _modifiers = modifiers;
        _vk = virtualKey;
        _onHotkeyPressed = onHotkeyPressed;

        _msgThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "GlobalHotkeyMessagePump"
        };
        _msgThread.SetApartmentState(ApartmentState.STA);
        _msgThread.Start();

        _windowReady.Wait(2000);
    }

    /// <summary>True nếu tổ hợp phím đã đăng ký thành công (không bị app khác chiếm).</summary>
    public bool IsRegistered => !_registerFailed;

    private void MessageLoop()
    {
        var hInstance = GetModuleHandle(null);

        _wndProcDelegate = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance     = hInstance,
            lpszClassName = "WarpGlobalHotkeyWnd"
        };
        RegisterClassEx(ref wc);

        _hWnd = CreateWindowEx(0, "WarpGlobalHotkeyWnd", "WARP Hotkey", 0,
            0, 0, 0, 0, new nint(-3) /* HWND_MESSAGE */, 0, hInstance, 0);

        _registerFailed = !RegisterHotKey(_hWnd, HOTKEY_ID, _modifiers, _vk);
        if (_registerFailed)
        {
            DiagnosticLogService.Trace("[GlobalHotkeyHelper] RegisterHotKey thất bại — tổ hợp phím có thể đã bị chiếm.");
        }

        _windowReady.Set();

        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && (int)wParam == HOTKEY_ID)
        {
            try { _onHotkeyPressed(); } catch { }
            return 0;
        }

        if (msg == WM_DESTROY)
        {
            UnregisterHotKey(hWnd, HOTKEY_ID);
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hWnd != 0) DestroyWindow(_hWnd);
    }
}
