// ============================================================
// Services/TcpTableHelper.cs
// Đọc bảng kết nối TCP kèm PID chủ sở hữu (GetExtendedTcpTable).
//
// .NET có sẵn IPGlobalProperties.GetActiveTcpConnections() nhưng KHÔNG
// cho biết tiến trình nào sở hữu kết nối. Muốn biết "client game PID X
// đã kết nối vào server chưa" thì bắt buộc phải gọi Win32 API này.
// ============================================================
using System.Net;
using System.Runtime.InteropServices;

namespace WarpGameAccelerator.Services;

public static class TcpTableHelper
{
    private const int AF_INET                 = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB    = 5;
    private const uint NO_ERROR               = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    /// <summary>
    /// Tiến trình <paramref name="pid"/> có kết nối TCP ESTABLISHED nào tới
    /// một địa chỉ công cộng (không phải LAN/loopback) hay không.
    /// Dùng để xác định client game đã thật sự vào được server chưa.
    /// </summary>
    public static bool HasEstablishedPublicConnection(int pid)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false,
                                              AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != ERROR_INSUFFICIENT_BUFFER && result != NO_ERROR) return false;
            if (size <= 0) return false;

            buffer = Marshal.AllocHGlobal(size);
            result = GetExtendedTcpTable(buffer, ref size, false,
                                         AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != NO_ERROR) return false;

            int entries = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = buffer + sizeof(int);

            for (int i = 0; i < entries; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr += rowSize;

                if (row.OwningPid != (uint)pid) continue;
                if (row.State != MIB_TCP_STATE_ESTAB) continue;
                if (IsPrivateOrLocal(row.RemoteAddr)) continue;

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Lọc bỏ địa chỉ LAN/loopback/TUN để chỉ tính kết nối ra server thật.
    /// 198.18.x.x là dải mà mihomo dùng cho TUN adapter (xem SKILL.md mục 7).
    /// </summary>
    private static bool IsPrivateOrLocal(uint addrNetworkOrder)
    {
        var bytes = BitConverter.GetBytes(addrNetworkOrder);
        byte a = bytes[0], b = bytes[1];

        if (a == 0 || a == 127) return true;                 // 0.0.0.0, loopback
        if (a == 10) return true;                            // 10.0.0.0/8
        if (a == 192 && b == 168) return true;               // 192.168.0.0/16
        if (a == 172 && b >= 16 && b <= 31) return true;     // 172.16.0.0/12
        if (a == 198 && (b == 18 || b == 19)) return true;   // 198.18.0.0/15 — TUN mihomo
        if (a == 169 && b == 254) return true;               // link-local

        return false;
    }

    /// <summary>Địa chỉ IP đích của kết nối đầu tiên khớp — dùng để ghi log.</summary>
    public static string? GetFirstRemoteAddress(int pid)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false,
                                              AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != ERROR_INSUFFICIENT_BUFFER && result != NO_ERROR) return null;
            if (size <= 0) return null;

            buffer = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(buffer, ref size, false,
                                    AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR) return null;

            int entries = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = buffer + sizeof(int);

            for (int i = 0; i < entries; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr += rowSize;

                if (row.OwningPid != (uint)pid) continue;
                if (row.State != MIB_TCP_STATE_ESTAB) continue;
                if (IsPrivateOrLocal(row.RemoteAddr)) continue;

                var ip   = new IPAddress(BitConverter.GetBytes(row.RemoteAddr));
                var port = ((row.RemotePort & 0xFF) << 8) | ((row.RemotePort >> 8) & 0xFF);
                return $"{ip}:{port}";
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }
}
