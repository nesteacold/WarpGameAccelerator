// ============================================================
// Services/ProcessCommandLine.cs
// Đọc CommandLine của tiến trình khác KHÔNG qua WMI.
//
// Lý do tồn tại (số liệu, không phải sở thích): `System.Management` từng treo
// "hàng chục phút" trên máy người dùng (ghi ngay trong comment của
// MultiClientService.GetFxgameExecutablePaths), và được đo là ném 11 exception
// trên 224 phiên, mỗi lần hỏng mất ~5s. WMI là một DỊCH VỤ — treo được.
// CommandLine thật ra nằm sẵn trong PEB của tiến trình đích, đọc bằng
// ReadProcessMemory là xong: không dịch vụ, không hàng đợi, không treo.
//
// Không cần biết tiến trình đích 32 hay 64-bit: xem comment ở các offset dưới — đó là kết quả ĐO,
// không phải giả định.
// ============================================================
using System.Runtime.InteropServices;

namespace WarpGameAccelerator.Services;

public static class ProcessCommandLine
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    // 64-bit: PEB.ProcessParameters = 0x20, params.CommandLine (UNICODE_STRING) = 0x70,
    // UNICODE_STRING = { Length u16, MaximumLength u16, Buffer ptr64 @ +0x08 }.
    //
    // ĐO 2026-11-01 (experiments/process-command-line, quét lưới offset trên hai tiến trình con có
    // marker đã biết): với target 32-bit, reader 64-bit tìm thấy đúng MỘT tổ hợp trúng và nó là
    // **cùng bộ offset 64-bit** (PEB+0x20 / params+0x70, con trỏ 8 byte); cách diễn giải 4 byte cho
    // 0/441 tổ hợp trúng. Nghĩa là kernel expose một PEB dạng 64-bit cho tiến trình đọc 64-bit, nên
    // KHÔNG cần nhánh Wow64 riêng — và trên Windows này fxgame.exe có cả hai bản (bin = i386,
    // bin64 = AMD64) nhưng MỘT đường đọc duy nhất là đủ cho cả hai.
    private const int Peb_ProcessParameters = 0x20;
    private const int Params_CommandLine = 0x70;
    private const int Us_BufferOffset = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritableAffinityMask;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    /// <returns>true nếu đọc được; false khi tiến trình đã thoát / khác quyền (vd app chạy
    /// elevated) / bộ nhớ không đọc được. Không bao giờ ném.</returns>
    public static bool TryGet(int pid, out string commandLine)
    {
        commandLine = string.Empty;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero) return false;

            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(h, 0 /*ProcessBasicInformation*/, ref pbi,
                                                   Marshal.SizeOf(pbi), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return false;

            return ReadFromPeb(h, pbi.PebBaseAddress, out commandLine);
        }
        catch
        {
            return false;      // đường này thay WMI nên bắt buộc: im lặng mà không treo
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    private static bool ReadFromPeb(IntPtr h, IntPtr peb, out string commandLine)
    {
        commandLine = string.Empty;

        if (!ReadIntPtrAt(h, new IntPtr(peb.ToInt64() + Peb_ProcessParameters), out IntPtr parameters))
            return false;
        if (parameters == IntPtr.Zero) return false;

        IntPtr unicodeString = new IntPtr(parameters.ToInt64() + Params_CommandLine);
        if (!ReadBytes(h, unicodeString, 16, out byte[] descriptor)) return false;

        ushort length = BitConverter.ToUInt16(descriptor, 0);
        if (length == 0) return false;

        long bufferAddr = BitConverter.ToInt64(descriptor, Us_BufferOffset);
        if (bufferAddr == 0) return false;

        if (!ReadBytes(h, new IntPtr(bufferAddr), length, out byte[] raw)) return false;
        commandLine = System.Text.Encoding.Unicode.GetString(raw, 0, Math.Min(raw.Length, length));
        return commandLine.Length > 0;
    }

    private static bool ReadIntPtrAt(IntPtr h, IntPtr addr, out IntPtr value)
    {
        if (!ReadBytes(h, addr, IntPtr.Size, out byte[] b))
        {
            value = IntPtr.Zero;
            return false;
        }
        long v = IntPtr.Size == 8 ? BitConverter.ToInt64(b, 0) : BitConverter.ToUInt32(b, 0);
        value = new IntPtr(v);
        return true;
    }

    private static bool ReadBytes(IntPtr h, IntPtr addr, int size, out byte[] buffer)
    {
        buffer = Array.Empty<byte>();
        if (addr == IntPtr.Zero || size <= 0 || size > 1 << 20) return false;
        var tmp = new byte[size];
        if (!ReadProcessMemory(h, addr, tmp, size, out int read) || read < size) return false;
        buffer = tmp;
        return true;
    }
}
