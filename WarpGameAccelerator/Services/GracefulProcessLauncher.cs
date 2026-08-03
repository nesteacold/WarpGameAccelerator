// ============================================================
// Services/GracefulProcessLauncher.cs
// Khởi chạy 1 tiến trình console trong console + process group RIÊNG, để
// có thể gửi CTRL_BREAK_EVENT chỉ nhắm đúng tiến trình đó.
//
// Process.Kill() của .NET gọi TerminateProcess() thẳng — mihomo (Go binary)
// không có cơ hội chạy cleanup handler của nó (gỡ route, khôi phục DNS, đóng
// TUN adapter) trước khi chết, để lại route/DNS bẩn trỏ vào adapter đã mất
// → máy mất internet sau khi Stop Boost. Muốn mihomo tự dọn, phải gửi tín
// hiệu ngắt (CTRL_BREAK_EVENT) mà GenerateConsoleCtrlEvent yêu cầu tiến
// trình đích phải có console riêng + process group riêng (CREATE_NEW_CONSOLE
// | CREATE_NEW_PROCESS_GROUP) — Process.Start() của .NET không cho set các
// cờ này nên phải gọi CreateProcess() trực tiếp qua P/Invoke.
// ============================================================
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WarpGameAccelerator.Services;

internal static class GracefulProcessLauncher
{
    private const uint CREATE_NEW_CONSOLE      = 0x00000010;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint STARTF_USESTDHANDLES    = 0x00000100;
    private const uint STARTF_USESHOWWINDOW    = 0x00000001;
    private const ushort SW_HIDE               = 0;
    private const uint HANDLE_FLAG_INHERIT     = 0x00000001;
    private const uint CTRL_BREAK_EVENT        = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed class LaunchedProcess : IDisposable
    {
        public int ProcessId { get; }
        public SafeFileHandle StdOutRead { get; }
        public SafeFileHandle StdErrRead { get; }

        private IntPtr _hProcess;
        private IntPtr _hThread;

        internal LaunchedProcess(int pid, IntPtr hProcess, IntPtr hThread,
            SafeFileHandle stdOutRead, SafeFileHandle stdErrRead)
        {
            ProcessId = pid;
            _hProcess = hProcess;
            _hThread = hThread;
            StdOutRead = stdOutRead;
            StdErrRead = stdErrRead;
        }

        /// <summary>Gửi Ctrl+Break riêng cho process group của tiến trình này.</summary>
        public bool TrySendCtrlBreak() => GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)ProcessId);

        public void Dispose()
        {
            if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
            if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
            StdOutRead.Dispose();
            StdErrRead.Dispose();
        }
    }

    /// <summary>
    /// Start <paramref name="fileName"/> trong console ẩn + process group riêng.
    /// stdout/stderr được redirect qua pipe (đọc bằng StreamReader như bình
    /// thường); console riêng chỉ để làm mục tiêu hợp lệ cho
    /// GenerateConsoleCtrlEvent, không hiển thị (SW_HIDE).
    /// </summary>
    public static LaunchedProcess Start(string fileName, string arguments, string workingDirectory)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out var stdOutRead, out var stdOutWrite, ref sa, 0))
            throw new Exception($"CreatePipe(stdout) lỗi: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out var stdErrRead, out var stdErrWrite, ref sa, 0))
        {
            stdOutRead.Dispose();
            stdOutWrite.Dispose();
            throw new Exception($"CreatePipe(stderr) lỗi: {Marshal.GetLastWin32Error()}");
        }

        // Đầu đọc chỉ dùng ở phía cha — không cho tiến trình con kế thừa.
        SetHandleInformation(stdOutRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(stdErrRead, HANDLE_FLAG_INHERIT, 0);

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
            wShowWindow = SW_HIDE,
            hStdOutput = stdOutWrite.DangerousGetHandle(),
            hStdError = stdErrWrite.DangerousGetHandle(),
            hStdInput = IntPtr.Zero
        };

        var commandLine = $"\"{fileName}\" {arguments}";
        var creationFlags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;

        bool ok = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
            creationFlags, IntPtr.Zero, workingDirectory, ref si, out var pi);

        // Bản kế thừa của đầu ghi đã sang tay tiến trình con — đóng bản ở cha.
        stdOutWrite.Dispose();
        stdErrWrite.Dispose();

        if (!ok)
        {
            stdOutRead.Dispose();
            stdErrRead.Dispose();
            throw new Exception($"CreateProcess lỗi: {Marshal.GetLastWin32Error()}");
        }

        return new LaunchedProcess(pi.dwProcessId, pi.hProcess, pi.hThread, stdOutRead, stdErrRead);
    }
}
