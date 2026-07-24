// ============================================================
// Helpers/TrayIconHelper.cs — System Tray icon via P/Invoke Shell_NotifyIcon
// Không dùng WinForms để tránh xung đột với WinUI 3 build targets.
// ============================================================
using System.Runtime.InteropServices;
using System.Text;

namespace WarpGameAccelerator.Helpers;

/// <summary>
/// Quản lý system tray icon dùng Win32 Shell_NotifyIcon (P/Invoke).
/// Chạy message pump trên thread riêng để nhận WM_TRAYMESSAGE.
/// </summary>
public sealed class TrayIconHelper : IDisposable
{
    // ── Win32 Constants ─────────────────────────────────────
    private const int WM_APP         = 0x8000;
    private const int WM_TRAY        = WM_APP + 1;
    private const int WM_DESTROY     = 0x0002;
    private const int NIM_ADD        = 0x00000000;
    private const int NIM_MODIFY     = 0x00000001;
    private const int NIM_DELETE     = 0x00000002;
    private const int NIF_MESSAGE    = 0x00000001;
    private const int NIF_ICON       = 0x00000002;
    private const int NIF_TIP        = 0x00000004;
    private const int NIF_INFO       = 0x00000010;
    private const int NIIF_INFO      = 0x00000001;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int TPM_BOTTOMALIGN  = 0x0020;
    private const int TPM_RIGHTALIGN   = 0x0008;
    private const int MF_STRING      = 0x00000000;
    private const int MF_SEPARATOR   = 0x00000800;
    private const int MF_GRAYED      = 0x00000001;
    private const int WM_COMMAND     = 0x0111;
    private const int IDM_SHOW       = 1001;
    private const int IDM_DISCONNECT = 1002;
    private const int IDM_EXIT       = 1003;

    // ── Win32 Structs ────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int     cbSize;
        public nint    hWnd;
        public uint    uID;
        public uint    uFlags;
        public uint    uCallbackMessage;
        public nint    hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string  szTip;
        public uint    dwState;
        public uint    dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string  szInfo;
        public uint    uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string  szInfoTitle;
        public uint    dwInfoFlags;
        public Guid    guidItem;
        public nint    hBalloonIcon;
    }

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
    private struct POINT { public int X, Y; }

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

    // ── P/Invoke ────────────────────────────────────────────
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

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

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(nint hMenu, uint uFlags, int x, int y,
        int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    // ── Fields ───────────────────────────────────────────────
    private nint _hWnd;
    private NOTIFYICONDATA _nid;
    private readonly Thread _msgThread;
    private readonly Action _onShowWindow;
    private readonly Action _onDisconnect;
    private readonly Action _onExit;
    private bool _disposed;
    private WndProcDelegate? _wndProcDelegate; // Keep alive!
    private bool _isConnected;

    // ── Constructor ──────────────────────────────────────────
    public TrayIconHelper(Action onShowWindow, Action onDisconnect, Action onExit)
    {
        _onShowWindow = onShowWindow;
        _onDisconnect = onDisconnect;
        _onExit       = onExit;

        _msgThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "TrayIconMessagePump"
        };
        _msgThread.SetApartmentState(ApartmentState.STA);
        _msgThread.Start();
    }

    // ── Message Loop (runs on dedicated STA thread) ──────────
    private void MessageLoop()
    {
        var hInstance = GetModuleHandle(null);

        // Register hidden window class
        _wndProcDelegate = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize      = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance   = hInstance,
            lpszClassName = "WarpTrayWnd"
        };
        RegisterClassEx(ref wc);

        // Create hidden message-only window
        _hWnd = CreateWindowEx(0, "WarpTrayWnd", "WARP Tray", 0,
            0, 0, 0, 0, new nint(-3) /* HWND_MESSAGE */, 0, hInstance, 0);

        // Load custom icon
        var customIcon = LoadImage(0, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"), IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        if (customIcon == 0) customIcon = LoadIcon(0, new nint(32512)); // IDI_APPLICATION fallback

        // Add tray icon
        _nid = new NOTIFYICONDATA
        {
            cbSize          = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd            = _hWnd,
            uID             = 1,
            uFlags          = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = (uint)WM_TRAY,
            hIcon           = customIcon,
            szTip           = "WARP Game Accelerator"
        };
        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // Standard message pump
        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // ── Window Procedure ─────────────────────────────────────
    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TRAY)
        {
            var notifyMsg = (int)(lParam & 0xFFFF);

            if (notifyMsg == WM_LBUTTONDBLCLK)
                _onShowWindow();

            if (notifyMsg == WM_RBUTTONUP)
                ShowContextMenu(hWnd);

            return 0;
        }

        if (msg == WM_COMMAND)
        {
            var cmdId = (int)(wParam & 0xFFFF);
            switch (cmdId)
            {
                case IDM_SHOW:       _onShowWindow();  break;
                case IDM_DISCONNECT: _onDisconnect();  break;
                case IDM_EXIT:       _onExit();        break;
            }
            return 0;
        }

        if (msg == WM_DESTROY)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu(nint hWnd)
    {
        GetCursorPos(out var pt);
        SetForegroundWindow(hWnd);

        var hMenu = CreatePopupMenu();
        AppendMenu(hMenu, MF_STRING,    IDM_SHOW,       "Mở WARP Game Accelerator");
        AppendMenu(hMenu, MF_SEPARATOR, 0,              null);

        uint disconnectFlags = (uint)(MF_STRING | (_isConnected ? 0 : MF_GRAYED));
        AppendMenu(hMenu, disconnectFlags, IDM_DISCONNECT, "Ngắt kết nối WARP");
        AppendMenu(hMenu, MF_SEPARATOR, 0,              null);
        AppendMenu(hMenu, MF_STRING,    IDM_EXIT,       "Thoát");

        TrackPopupMenu(hMenu,
            TPM_BOTTOMALIGN | TPM_RIGHTALIGN,
            pt.X, pt.Y, 0, hWnd, 0);
        DestroyMenu(hMenu);
    }

    // ── Public API ───────────────────────────────────────────

    public void SetConnected(bool connected)
    {
        _isConnected = connected;
        _nid.szTip = connected
            ? "WARP Game Accelerator — Đang tăng tốc 🚀"
            : "WARP Game Accelerator — Chưa kết nối";
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    public void ShowBalloon(string title, string message)
    {
        _nid.uFlags    |= (uint)NIF_INFO;
        _nid.szInfoTitle = title;
        _nid.szInfo      = message;
        _nid.dwInfoFlags = (uint)NIIF_INFO;
        _nid.uTimeout    = 3000;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hWnd != 0) DestroyWindow(_hWnd);
    }
}
