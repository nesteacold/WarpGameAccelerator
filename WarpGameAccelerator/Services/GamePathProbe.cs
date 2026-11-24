// ============================================================
// Services/GamePathProbe.cs — chủ động hỏi "đích game có tới được từ tunnel không"
//
// Lý do tồn tại (số liệu, xem docs/game-path-blocked-2026-11-01.md): chỉ báo "SERVER GAME: có lỗi
// kết nối" hiện tại là THỤ ĐỘNG — nó cần mihomo ghi được một lần dial fail trong 15 giây gần nhất.
// Client đứng yên trong map (đúng triệu chứng đã ghi: "đứng yên thì bình thường, thoát/vào lại mới
// rớt") ⇒ không có dial ⇒ không có fail ⇒ chỉ báo báo khoẻ dù đường đã chết. Sự cố 2026-11-01 kéo
// dài ≥ 72 phút, 88/88 mẫu fail trên cả hai đường, mà dashboard không hề nhấp đỏ nếu không có client
// nào retry.
//
// Cách hỏi: TCP-connect tới đích game QUA SOCKS cục bộ của mihomo (config thật đo được: socks-port
// 7891). Không cần client game nào chạy, không phụ thuộc hàng đợi của game.
//
// Lớp này PHÂN BIỆT các loại hỏng vì chúng cần thông báo khác nhau — đây là chỗ app đã khuyên sai:
//   SocksUnavailable   → tunnel/mihomo chưa chạy (khuyên Stop/Start Boost là ĐÚNG ở ca này)
//   UnreachableByProxy → proxy nói "không tới được đích" (restart Boost là VÔ ÍCH — đã chứng minh)
//   Timeout            → gửi yêu cầu rồi im lặng, khớp "mọi cổng drop im lặng" đã đo
//   Reachable          → đường sống
// ============================================================
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WarpGameAccelerator.Services;

public enum GamePathVerdict
{
    Reachable,
    UnreachableByProxy,
    Timeout,
    SocksUnavailable,
    BadReply,
}

public static class GamePathProbe
{
    public static async Task<(GamePathVerdict Verdict, int RttMs, string Detail)> TryAsync(
        string socksEndpoint, string targetEndpoint, int timeoutMs = 4000, CancellationToken token = default)
    {
        var (socksHost, socksPort) = Parse(socksEndpoint, 7891);
        var (targetHost, targetPort) = Parse(targetEndpoint, 4000);
        if (string.IsNullOrEmpty(targetHost))
            return (GamePathVerdict.BadReply, -1, "đích không hợp lệ");

        using var tcp = new TcpClient();
        var connectWatch = Stopwatch.StartNew();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(socksHost, socksPort, connectCts.Token);
        }
        catch (Exception ex)
        {
            // Mọi thất bại ở BƯỚC KẾT NỐI đều là SocksUnavailable, KHÔNG phải Timeout.
            // Lý do có số liệu: connect vào một port ĐÓNG trên loopback ở Windows không trả RST
            // ngay mà delay ~2078 ms (đo 2026-11-01) rồi mới SocketException — nên với ngưỡng
            // ngắn hơn, ngoại lệ nổi lên là OperationCanceledException. Nếu phân loại theo kiểu
            // "bị cancel = Timeout" thì app sẽ báo "đích im lặng" trong khi sự thật là
            // mihomo chưa nghe máy, tức khuyên sai cho một nguyên nhân khác.
            // Peer ở đây là 127.0.0.1: không có chuyện "đường truyền chậm".
            string kind = ex is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
            return (GamePathVerdict.SocksUnavailable, -1,
                     $"không kết nối được SOCKS cục bộ {socksHost}:{socksPort} sau {connectWatch.ElapsedMilliseconds}ms ({kind}) — mihomo chưa nghe máy");
        }

        try
        {
            var stream = tcp.GetStream();

            // greeting: VER=5, một method, method 0 = không xác thực
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
            byte[] sel = new byte[2];
            var selRead = await ReadExact(stream, sel, 2, timeoutMs, token);
            if (selRead == Read.TimedOut) return (GamePathVerdict.Timeout, -1, "proxy im lặng ở bước chọn method");
            if (selRead == Read.Closed) return (GamePathVerdict.BadReply, -1, "proxy đóng socket ở bước chọn method");
            if (sel[0] != 0x05) return (GamePathVerdict.BadReply, -1, $"VER={sel[0]} không phải 5");
            if (sel[1] != 0x00) return (GamePathVerdict.BadReply, -1, $"proxy đòi xác thực (METHOD={sel[1]})");

            var req = new List<byte> { 0x05, 0x01, 0x00 };   // VER CMD=CONNECT RSV
            if (IPAddress.TryParse(targetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                req.Add(0x01);                                // ATYP IPv4
                req.AddRange(ip.GetAddressBytes());
            }
            else
            {
                req.Add(0x03);                                // ATYP domain — chính proxy phân giải
                var bytes = System.Text.Encoding.ASCII.GetBytes(targetHost);
                req.Add((byte)bytes.Length);
                req.AddRange(bytes);
            }
            req.Add((byte)(targetPort >> 8));
            req.Add((byte)(targetPort & 0xFF));

            var started = Stopwatch.StartNew();
            await stream.WriteAsync(req.ToArray(), token);

            byte[] rep = new byte[5];   // VER REP RSV ATYP + byte đầu của BND.ADDR
            var repRead = await ReadExact(stream, rep, 5, timeoutMs, token);
            if (repRead == Read.TimedOut)
                return (GamePathVerdict.Timeout, -1,
                         $"proxy im lặng khi được yêu cầu nối {targetHost}:{targetPort}");
            if (repRead == Read.Closed)
                // mihomo cắt kết nối ngay khi dial thất bại — đây là chữ ký PHỔ BIẾN nhất của
                // "đích không tới được", không phải lỗi của SOCKS.
                return (GamePathVerdict.UnreachableByProxy, -1,
                         $"proxy cắt kết nối sau yêu cầu nối {targetHost}:{targetPort} (dial thất bại)");
            if (rep[0] != 0x05) return (GamePathVerdict.BadReply, -1, $"VER={rep[0]} trong đáp lệ CONNECT");

            started.Stop();
            byte code = rep[1];
            if (code == 0x00)
                return (GamePathVerdict.Reachable, (int)started.ElapsedMilliseconds,
                         $"{targetHost}:{targetPort} nối được qua tunnel");

            // Không cần đọc nốt BND.ADDR/BND.PORT: đã có kết luận, và socket sẽ đóng.
            string why = code switch
            {
                0x01 => "proxy cấm vì quy tắc chung",
                0x02 => "lỗi quy tắc của proxy",
                0x04 => $"đích {targetHost} không tới được từ proxy",
                0x05 => "đích không hỗ trợ ATYP này",
                _ => $"proxy REP=0x{code:X2}"
            };
            return (GamePathVerdict.UnreachableByProxy, -1, why);
        }
        catch (Exception ex)
        {
            return (GamePathVerdict.BadReply, -1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Ba trạng thái, vì hai chuyện này khác nhau với người đọc thông báo:
    //   TimedOut  = proxy im lặng (đường sau proxy có vấn đề)
    //   Closed    = proxy cắt giữa handshake (chính SOCKS hỏng / nói chuyện sai kiểu)
    private enum Read { Ok, Closed, TimedOut }

    private static async Task<Read> ReadExact(NetworkStream s, byte[] buf, int need, int timeoutMs, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeoutMs);
        int got = 0;
        try
        {
            while (got < need)
            {
                int n = await s.ReadAsync(buf.AsMemory(got, need - got), cts.Token);
                if (n <= 0) return Read.Closed;
                got += n;
            }
            return Read.Ok;
        }
        catch (OperationCanceledException) { return Read.TimedOut; }
        catch (IOException) { return Read.Closed; }
    }

    private static (string Host, int Port) Parse(string endpoint, int defaultPort)
    {
        endpoint = (endpoint ?? string.Empty).Trim();
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return (endpoint, defaultPort);
        var host = endpoint[..colon];
        return int.TryParse(endpoint[(colon + 1)..], out int port) ? (host, port) : (host, defaultPort);
    }
}
