// ============================================================
// Services/TcpTableHelper.cs
// Đọc bảng kết nối TCP kèm PID chủ sở hữu (GetExtendedTcpTable).
//
// .NET có sẵn IPGlobalProperties.GetActiveTcpConnections() nhưng KHÔNG
// cho biết tiến trình nào sở hữu kết nối. Muốn biết "client game PID X
// đã kết nối vào server chưa" thì bắt buộc phải gọi Win32 API này.
// ============================================================
using System.Net;
using System.Text;
using System.Runtime.InteropServices;

namespace WarpGameAccelerator.Services;

public static class TcpTableHelper
{
    private const int AF_INET                 = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_LISTEN   = 2;
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

    /// <summary>Chẩn đoán cho LÚC THẤT BẠI: mô tả trung thực những gì bảng TCP thấy, vì
    /// `HasEstablishedPublicConnection == false` có thể nghĩa là (a) chưa có kết nối nào,
    /// (b) đã có nhưng chỉ tới địa chỉ LAN/loopback/TUN, hoặc (c) còn ở trạng thái SYN.
    /// Không có hàm này thì 124 dòng "chưa thấy kết nối server" trong trace.log là
    /// điểm mù tuyệt đối (đo 2026-11-01: 491 dòng THÀNH CÔNG có ghi đích, 124 dòng THẤT BẠI
    /// không ghi gì để quy nguyên nhân).</summary>
    public static string DescribeConnections(int pid)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false,
                                              AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != ERROR_INSUFFICIENT_BUFFER && result != NO_ERROR) return "bang TCP: loi " + result;
            if (size <= 0) return "bang TCP: rong";

            buffer = Marshal.AllocHGlobal(size);
            result = GetExtendedTcpTable(buffer, ref size, false,
                                         AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != NO_ERROR) return "bang TCP: loi " + result;

            int entries = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = buffer + sizeof(int);

            var states = new Dictionary<uint, int>();
            var sample = new List<string>(4);
            int total = 0, priv = 0;
            for (int i = 0; i < entries; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr += rowSize;
                if (row.OwningPid != (uint)pid) continue;
                total++;
                states.TryGetValue(row.State, out int c);
                states[row.State] = c + 1;
                bool isPriv = IsPrivateOrLocal(row.RemoteAddr);
                if (isPriv) priv++;
                if (sample.Count < 4)
                    sample.Add($"{FmtAddr(row.RemoteAddr)}:{(row.RemotePort & 0xFF) << 8 | row.RemotePort >> 8}" +
                               (isPriv ? "[riêng]" : "[công]") + $" {StateName(row.State)}");
            }

            if (total == 0) return "khong co ket noi TCP nao cua PID nay";
            var sb = new StringBuilder($"ket noi={total} (rieng={priv})");
            foreach (var kv in states.OrderByDescending(k => k.Value))
                sb.Append($" {StateName(kv.Key)}x{kv.Value}");
            sb.Append(" | ").Append(string.Join(", ", sample));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "loi chan doan: " + ex.GetType().Name;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string StateName(uint s) => s switch
    {
        0 => "UNSPEC", 1 => "CLOSED", 2 => "LISTEN", 3 => "SYN-SENT", 4 => "SYN-RCVD",
        5 => "ESTAB", 6 => "FIN-WAIT1", 7 => "FIN-WAIT2", 8 => "TIME-WAIT",
        9 => "CLOSE-WAIT", 10 => "LAST-ACK", 11 => "CLOSING", 12 => "DELETE-TCB",
        _ => "state" + s
    };

    private static string FmtAddr(uint networkOrder)
    {
        var b = BitConverter.GetBytes(networkOrder);
        return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
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

    /// <summary>
    /// Batch 68: khi client chỉ có kết nối loopback, ai đang thực sự chở traffic của nó?
    /// Chuỗi: client → ESTAB 127.0.0.1:P (P là cổng LISTEN của tiến trình khác) → chủ của P có
    /// ESTAB ra địa chỉ công hay không. Mẫu SỐNG đo được trên máy dev 01-11:
    /// 5 fxgame → 127.0.0.1:12857 → gearup_booster (pid 37840) → 13 kết nối công (11× :2134).
    /// Trả chuỗi ngắn (≤ ~90 ký tự), im lặng khi không có gì để nói.
    /// </summary>
    public static string DescribeForwarderChain(int pid)
    {
        try
        {
            var rows = ReadRows();
            if (rows is null) return "chuoi:khong doc duoc bang";

            var ports = new HashSet<int>();
            foreach (var r in rows)
            {
                if (r.OwningPid != (uint)pid || r.State != MIB_TCP_STATE_ESTAB) continue;
                if (!IsPrivateOrLocal(r.RemoteAddr)) continue;
                int rp = (int)((r.RemotePort & 0xFF) << 8 | r.RemotePort >> 8);
                if (rp > 0) ports.Add(rp);
            }
            if (ports.Count == 0) return "chuoi:khong co loopback";

            uint owner = 0; int ownerPort = 0;
            foreach (var r in rows)
            {
                if (r.State != MIB_TCP_STATE_LISTEN) continue;
                int lp = (int)((r.LocalPort & 0xFF) << 8 | r.LocalPort >> 8);
                if (!ports.Contains(lp) || r.OwningPid == (uint)pid) continue;
                owner = r.OwningPid; ownerPort = lp; break;
            }
            if (owner == 0) return $"chuoi:khong ai listen {string.Join("/", ports)}";

            int pub = 0;
            foreach (var r in rows)
            {
                if (r.OwningPid != owner || r.State != MIB_TCP_STATE_ESTAB) continue;
                if (IsPrivateOrLocal(r.RemoteAddr)) continue;
                pub++;
            }
            string name;
            try { name = System.Diagnostics.Process.GetProcessById((int)owner).ProcessName; }
            catch { name = "pid" + owner; }
            return $"chuoi:{ownerPort}={name},{pub}cong";
        }
        catch (Exception ex) { return "chuoi loi " + ex.GetType().Name; }
    }

    /// <summary>Toàn bộ bảng TCP/IPv4 một lần — dùng cho các phép cần NHIỀU lần quét.</summary>
    private static List<MibTcpRowOwnerPid>? ReadRows()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint r = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (r != ERROR_INSUFFICIENT_BUFFER && r != NO_ERROR) return null;
            if (size <= 0) return null;
            buffer = Marshal.AllocHGlobal(size);
            r = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (r != NO_ERROR) return null;
            int entries = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var list = new List<MibTcpRowOwnerPid>(entries);
            IntPtr rowPtr = buffer + sizeof(int);
            for (int i = 0; i < entries; i++)
            {
                list.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr));
                rowPtr += rowSize;
            }
            return list;
        }
        catch { return null; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
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
