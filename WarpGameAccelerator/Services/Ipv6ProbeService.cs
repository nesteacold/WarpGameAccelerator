using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Kiểm tra nhanh máy hiện tại có đường IPv6 native tới Cloudflare MASQUE hay không.
///
/// Lý do cần (đo 2026-11-24 trên đường truyền VN):
///   - IPv6 tới Cloudflare MASQUE (2606:4700:103::1 / 104::1) có ~54,5% cơ hội
///     nhận colo HKG và RTT ingress ~27 ms (so với ~92 ms IPv4 SIN), giúp giảm
///     76–91 ms đi HK/TW.
///   - Nhưng nhiều ISP/router không có IPv6 native — nếu mù quáng đưa IPv6 vào
///     pool ở các máy đó sẽ sinh dial fail vô ích. Kiểm tra trong ≤1,5 s trước khi
///     dựng pool giúp tự bật trên máy có IPv6 và fallback sạch về IPv4 trên máy không có.
/// </summary>
public static class Ipv6ProbeService
{
    private const string ProbeHost = "2606:4700:103::1";
    private const int ProbePort = 443;

    /// <summary>
    /// Trả về true nếu máy có ít nhất một địa chỉ IPv6 global unicast trên NIC đang Up
    /// VÀ mở được kết nối TCP tới Cloudflare MASQUE IPv6 trong <paramref name="timeoutMs"/>.
    /// Không bao giờ throw.
    /// </summary>
    public static async Task<bool> HasNativeIpv6Async(int timeoutMs = 1500, CancellationToken ct = default)
    {
        if (!HasGlobalUnicastIpv6Address())
            return false;

        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetworkV6);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(IPAddress.Parse(ProbeHost), ProbePort, linked.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kiểm tra nhanh (không tốn mạng) xem máy có địa chỉ IPv6 global unicast nào không.</summary>
    public static bool HasGlobalUnicastIpv6Address()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (IsGlobalUnicastIpv6(uni.Address)) return true;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    internal static bool IsGlobalUnicastIpv6(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (IPAddress.IsLoopback(addr)) return false;
        if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast || addr.IsIPv4MappedToIPv6)
            return false;

        var bytes = addr.GetAddressBytes();
        // ULA fc00::/7 (bao gồm fd00::/8 của TUN/VPN nội bộ) không phải global Internet IPv6.
        if ((bytes[0] & 0xFE) == 0xFC) return false;
        // Global Unicast IPv6 nằm trong 2000::/3 (3 bit cao = 001).
        return (bytes[0] & 0xE0) == 0x20;
    }
}
