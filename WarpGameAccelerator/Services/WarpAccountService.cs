using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class WarpAccountInfo
{
    public string PrivateKey    { get; set; } = string.Empty;
    public string PublicKey     { get; set; } = string.Empty;
    public string Id            { get; set; } = string.Empty;
    public string Token         { get; set; } = string.Empty;
    public string License       { get; set; } = string.Empty;
    public string IPv4          { get; set; } = string.Empty;
    public string IPv6          { get; set; } = string.Empty;
    public string PeerPublicKey { get; set; } = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";
    public string Endpoint      { get; set; } = "162.159.192.1:2408";
}

public class WarpAccountService
{
    private static readonly string AccountFilePath = Path.Combine(AppContext.BaseDirectory, "Data", "warp_account.json");

    public static async Task<WarpAccountInfo> GetOrCreateAccountAsync()
    {
        // 1. Kiểm tra xem đã có tài khoản lưu sẵn chưa
        if (File.Exists(AccountFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(AccountFilePath);
                var acc = JsonSerializer.Deserialize<WarpAccountInfo>(json);
                if (acc != null && !string.IsNullOrEmpty(acc.PrivateKey) && !string.IsNullOrEmpty(acc.IPv4))
                {
                    return acc;
                }
            }
            catch { }
        }

        // 2. Tạo tài khoản WireGuard mới và đăng ký với Cloudflare WARP API
        var newAcc = await RegisterNewWarpAccountAsync();
        
        // 3. Lưu vào ổ đĩa để tái sử dụng
        try
        {
            var dir = Path.GetDirectoryName(AccountFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(newAcc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(AccountFilePath, json);
        }
        catch { }

        return newAcc;
    }

    private static async Task<WarpAccountInfo> RegisterNewWarpAccountAsync()
    {
        // Tạo 32 bytes random private key
        byte[] privateKeyBytes = RandomNumberGenerator.GetBytes(32);
        byte[] publicKeyBytes = X25519KeyGenerator.GetPublicKey(privateKeyBytes);

        string privKeyB64 = Convert.ToBase64String(X25519KeyGenerator.ClampPrivateKey(privateKeyBytes));
        string pubKeyB64 = Convert.ToBase64String(publicKeyBytes);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");

        var payload = new
        {
            install_id = "",
            fcm_token = "",
            tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            key = pubKeyB64,
            type = "Android",
            locale = "en_US"
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("https://api.cloudflareclient.com/v0i1909051800/reg", content);
        response.EnsureSuccessStatusCode();

        var jsonResp = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonResp);

        var result = new WarpAccountInfo
        {
            PrivateKey = privKeyB64,
            PublicKey = pubKeyB64
        };

        var root = doc.RootElement.TryGetProperty("result", out var resEl) ? resEl : doc.RootElement;

        // Lưu Id và Token để dùng gọi API sau này (nạp license key...)
        if (root.TryGetProperty("id", out var idEl)) result.Id = idEl.GetString() ?? "";
        if (root.TryGetProperty("token", out var tokenEl)) result.Token = tokenEl.GetString() ?? "";

        if (root.TryGetProperty("config", out var configEl))
        {
            if (configEl.TryGetProperty("interface", out var ifaceEl) &&
                ifaceEl.TryGetProperty("addresses", out var addrEl))
            {
                if (addrEl.TryGetProperty("v4", out var v4El)) result.IPv4 = v4El.GetString() ?? "172.16.0.2";
                if (addrEl.TryGetProperty("v6", out var v6El)) result.IPv6 = v6El.GetString() ?? "";
            }

            if (configEl.TryGetProperty("peers", out var peersEl) && peersEl.ValueKind == JsonValueKind.Array && peersEl.GetArrayLength() > 0)
            {
                var peer = peersEl[0];
                if (peer.TryGetProperty("public_key", out var pkEl)) result.PeerPublicKey = pkEl.GetString() ?? result.PeerPublicKey;
                if (peer.TryGetProperty("endpoint", out var epEl) && epEl.TryGetProperty("v4", out var epv4El))
                {
                    result.Endpoint = epv4El.GetString() ?? result.Endpoint;
                }
            }
        }

        if (string.IsNullOrEmpty(result.IPv4)) result.IPv4 = "172.16.0.2";
        return result;
    }

    // ── Cập nhật License Key WARP+ ────────────────────────────
    public static async Task<(bool Success, string Message)> UpdateLicenseAsync(string licenseKey)
    {
        try
        {
            var acc = await GetOrCreateAccountAsync();
            if (string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token))
                return (false, "Không tìm thấy ID hoặc Token tài khoản. Vui lòng xóa file warp_account.json và khởi động lại app.");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {acc.Token}");

            var payload = new { license = licenseKey };
            var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var url = $"https://api.cloudflareclient.com/v0i1909051800/reg/{acc.Id}/account";
            var response = await client.PutAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                acc.License = licenseKey;
                var dir = Path.GetDirectoryName(AccountFilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(AccountFilePath, json);
                return (true, "Kích hoạt WARP+ thành công!");
            }

            if ((int)response.StatusCode == 429)
                return (false, "Bạn đã thử quá nhiều lần. Vui lòng đợi vài phút rồi thử lại.");
            if ((int)response.StatusCode == 403)
                return (false, "License Key không hợp lệ hoặc đã hết hạn.");

            return (false, $"Lỗi từ server ({(int)response.StatusCode}). Vui lòng kiểm tra lại key.");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi kết nối: {ex.Message}");
        }
    }

    // ── Reset về WARP Free (xóa tài khoản cũ) ────────────────
    public static Task<(bool Success, string Message)> ResetToFreeAsync()
    {
        try
        {
            if (File.Exists(AccountFilePath))
                File.Delete(AccountFilePath);
            return Task.FromResult((true, "Đã xóa tài khoản cũ. Tài khoản WARP Free mới sẽ được tạo tự động khi bạn bấm Boost lần sau."));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, $"Không thể xóa file tài khoản: {ex.Message}"));
        }
    }
}


// ── X25519 Curve25519 Math ────────────────────────────────────
public static class X25519KeyGenerator
{
    public static byte[] ClampPrivateKey(byte[] key)
    {
        var res = (byte[])key.Clone();
        res[0] &= 248;
        res[31] &= 127;
        res[31] |= 64;
        return res;
    }

    public static byte[] GetPublicKey(byte[] privateKey)
    {
        byte[] basePoint = new byte[32];
        basePoint[0] = 9;
        return ScalarMult(ClampPrivateKey(privateKey), basePoint);
    }

    private static byte[] ScalarMult(byte[] scalar, byte[] point)
    {
        long[] x1 = Decode32(point);
        long[] x2 = new long[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        long[] z2 = new long[10];
        long[] x3 = (long[])x1.Clone();
        long[] z3 = new long[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        int swap = 0;
        for (int pos = 254; pos >= 0; --pos)
        {
            int bit = (scalar[pos >> 3] >> (pos & 7)) & 1;
            swap ^= bit;
            CSwap(x2, x3, swap);
            CSwap(z2, z3, swap);
            swap = bit;

            long[] a = Add(x2, z2);
            long[] b = Sub(x2, z2);
            long[] c = Add(x3, z3);
            long[] d = Sub(x3, z3);
            long[] da = Mul(d, a);
            long[] cb = Mul(c, b);
            x3 = Sq(Add(da, cb));
            z3 = Mul(x1, Sq(Sub(da, cb)));
            long[] aa = Sq(a);
            long[] bb = Sq(b);
            x2 = Mul(aa, bb);
            long[] e = Sub(aa, bb);
            z2 = Mul(e, Add(aa, MulScalar(e, 121665)));
        }
        CSwap(x2, x3, swap);
        CSwap(z2, z3, swap);
        return Encode32(Mul(x2, Inv(z2)));
    }

    private static void CSwap(long[] a, long[] b, int swap)
    {
        long mask = -swap;
        for (int i = 0; i < 10; ++i)
        {
            long t = mask & (a[i] ^ b[i]);
            a[i] ^= t;
            b[i] ^= t;
        }
    }

    private static long[] Decode32(byte[] b)
    {
        long[] out1 = new long[10];
        for (int i = 0; i < 10; ++i)
        {
            int bitIdx = i * 26;
            int byteIdx = bitIdx >> 3;
            int shift = bitIdx & 7;
            ulong val = 0;
            for (int j = 0; j < 4 && (byteIdx + j) < 32; ++j)
                val |= ((ulong)b[byteIdx + j]) << (j * 8);
            out1[i] = (long)((val >> shift) & 0x3FFFFFF);
        }
        return out1;
    }

    private static byte[] Encode32(long[] elem)
    {
        long[] f = (long[])elem.Clone();
        Carry(f); Carry(f); Carry(f);
        long[] g = Sub(f, new long[10] { 0x3FFFFED, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF, 0x3FFFFFF });
        Carry(g);
        long mask = g[9] >> 26;
        for (int i = 0; i < 10; ++i) g[i] = f[i] ^ (mask & (f[i] ^ g[i]));

        byte[] b = new byte[32];
        for (int i = 0; i < 10; ++i)
        {
            int bitIdx = i * 26;
            int byteIdx = bitIdx >> 3;
            int shift = bitIdx & 7;
            ulong val = (ulong)g[i] << shift;
            for (int j = 0; j < 4 && (byteIdx + j) < 32; ++j)
                b[byteIdx + j] |= (byte)((val >> (j * 8)) & 0xFF);
        }
        return b;
    }

    private static void Carry(long[] a)
    {
        for (int i = 0; i < 9; ++i)
        {
            long carry = a[i] >> 26;
            a[i] &= 0x3FFFFFF;
            a[i + 1] += carry;
        }
        long c9 = a[9] >> 25;
        a[9] &= 0x1FFFFFF;
        a[0] += c9 * 19;
    }

    private static long[] Add(long[] a, long[] b)
    {
        long[] res = new long[10];
        for (int i = 0; i < 10; ++i) res[i] = a[i] + b[i];
        return res;
    }

    private static long[] Sub(long[] a, long[] b)
    {
        long[] res = new long[10];
        for (int i = 0; i < 10; ++i) res[i] = a[i] - b[i] + 0x7FFFFDA;
        return res;
    }

    private static long[] MulScalar(long[] a, long b)
    {
        long[] res = new long[10];
        for (int i = 0; i < 10; ++i) res[i] = a[i] * b;
        Carry(res);
        return res;
    }

    private static long[] Mul(long[] a, long[] b)
    {
        long[] res = new long[20];
        for (int i = 0; i < 10; ++i)
            for (int j = 0; j < 10; ++j)
                res[i + j] += a[i] * b[j];
        for (int i = 0; i < 9; ++i) res[i] += res[i + 10] * 38;
        res[9] += res[19] * 38;
        long[] out1 = new long[10];
        Array.Copy(res, out1, 10);
        Carry(out1);
        return out1;
    }

    private static long[] Sq(long[] a) => Mul(a, a);

    private static long[] Inv(long[] z)
    {
        long[] z2 = Sq(z);
        long[] z9 = Mul(z2, z);
        long[] z11 = Mul(z9, z2);
        long[] z2_5_0 = Mul(Sq(z11), z9);
        long[] z2_10_0 = Mul(SqN(z2_5_0, 5), z2_5_0);
        long[] z2_20_0 = Mul(SqN(z2_10_0, 10), z2_10_0);
        long[] z2_50_0 = Mul(SqN(z2_20_0, 20), z2_20_0);
        long[] z2_100_0 = Mul(SqN(z2_50_0, 50), z2_50_0);
        long[] z2_200_0 = Mul(SqN(z2_100_0, 100), z2_100_0);
        long[] z2_250_0 = Mul(SqN(z2_200_0, 50), z2_50_0);
        return Mul(SqN(z2_250_0, 5), z11);
    }

    private static long[] SqN(long[] a, int n)
    {
        long[] res = a;
        for (int i = 0; i < n; ++i) res = Sq(res);
        return res;
    }
}
