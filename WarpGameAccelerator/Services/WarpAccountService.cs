using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public string ClientId      { get; set; } = string.Empty;
    /// <summary>Đã đặt tên thiết bị trên Cloudflare (phân biệt trong app 1.1.1.1) hay chưa.</summary>
    public bool   DeviceNameSet { get; set; } = false;
}

/// <summary>
/// Tài khoản riêng cho Direct Mode MASQUE (Beta) — HOÀN TOÀN độc lập với
/// <see cref="WarpAccountInfo"/> (dùng cho Direct WireGuard). Đăng ký thiết
/// bị riêng, key ECDSA riêng, lưu file riêng — không đụng/không ảnh hưởng
/// tới tài khoản WireGuard đang dùng.
/// </summary>
public class WarpMasqueAccountInfo
{
    public string Id            { get; set; } = string.Empty;
    public string Token         { get; set; } = string.Empty;
    public string License       { get; set; } = string.Empty;
    /// <summary>ECDSA P-256 private key, PKCS8 DER, base64.</summary>
    public string PrivateKey    { get; set; } = string.Empty;
    /// <summary>Public key của Cloudflare relay (để pin/verify) — ECDSA P-256 SPKI DER, base64.</summary>
    public string PeerPublicKey { get; set; } = string.Empty;
    public string IPv4          { get; set; } = string.Empty;
    public string IPv6          { get; set; } = string.Empty;
    public string Server        { get; set; } = "consumer-masque.cloudflareclient.com";
    public int    Port          { get; set; } = 443;
    /// <summary>Đã đặt tên thiết bị trên Cloudflare (phân biệt trong app 1.1.1.1) hay chưa.</summary>
    public bool   DeviceNameSet { get; set; } = false;
}

public class WarpAccountService
{
    // Lưu vào AppData thay vì cạnh .exe → tồn tại qua mọi lần cập nhật
    private static readonly string AccountFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "warp_account.json");

    private static readonly string CoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Core");
    private static readonly string WgcfExePath = Path.Combine(CoreDir, "wgcf.exe");

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
                    if (!string.IsNullOrEmpty(acc.ClientId) && !IsZeroClientId(acc.ClientId))
                    {
                        await EnsureDeviceNamedAsync(acc);
                        return acc;
                    }

                    // ClientId thiếu/toàn số 0 (bug cũ của luồng wgcf) — Cloudflare
                    // dùng đúng 3 byte này ở tầng edge để gắn chính sách QoS/WARP+
                    // cho từng session; để 0 khiến session bị coi như "không định
                    // danh" và có thể bị áp QoS mặc định thấp hơn dù tài khoản là
                    // WARP+ thật. Vá lại bằng cách hỏi thẳng Cloudflare, GIỮ
                    // nguyên key/thiết bị đã đăng ký — không cần đăng ký lại.
                    if (!string.IsNullOrEmpty(acc.Id) && !string.IsNullOrEmpty(acc.Token))
                    {
                        var realClientId = await FetchClientIdAsync(acc.Id, acc.Token);
                        if (!string.IsNullOrEmpty(realClientId))
                        {
                            acc.ClientId = realClientId;
                            try
                            {
                                var patchedJson = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
                                await File.WriteAllTextAsync(AccountFilePath, patchedJson);
                            }
                            catch { }
                            await EnsureDeviceNamedAsync(acc);
                            return acc;
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Tạo tài khoản WireGuard mới và đăng ký với Cloudflare WARP API
        //    Trước khi tạo mới: lưu License cũ lại để re-apply sau
        string oldLicense = string.Empty;
        if (File.Exists(AccountFilePath))
        {
            try
            {
                var oldJson = await File.ReadAllTextAsync(AccountFilePath);
                var oldAcc  = JsonSerializer.Deserialize<WarpAccountInfo>(oldJson);
                if (oldAcc != null) oldLicense = oldAcc.License ?? string.Empty;
            }
            catch { }
        }

        // Ưu tiên đăng ký qua wgcf: tài khoản tự đăng ký thẳng qua API (v0a1922)
        // vẫn hợp lệ nhưng bị Cloudflare xếp vào nhóm chính sách MASQUE (mihomo
        // không hỗ trợ) — wgcf hiện vẫn được Cloudflare cấp WireGuard cổ điển.
        var newAcc = await RegisterViaWgcfAsync() ?? await RegisterNewWarpAccountAsync();

        // 3. Lưu vào AppData
        try
        {
            var dir = Path.GetDirectoryName(AccountFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(newAcc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(AccountFilePath, json);
        }
        catch { }

        // 4. Nếu có License cũ → tự động re-apply (tránh mất key sau update)
        if (!string.IsNullOrEmpty(oldLicense))
        {
            await UpdateLicenseAsync(oldLicense);
        }

        await EnsureDeviceNamedAsync(newAcc);
        return newAcc;
    }

    /// <summary>
    /// Hỏi thẳng Cloudflare tài khoản đã thực sự là WARP+ hay chưa. Field
    /// <see cref="WarpAccountInfo.License"/> KHÔNG dùng để suy ra điều này —
    /// Cloudflare gán license_key cho mọi thiết bị đăng ký (cả tài khoản Free,
    /// dùng cho mục đích referral), nên có giá trị License không đồng nghĩa
    /// đã là WARP+.
    /// </summary>
    public static async Task<(bool WarpPlus, string AccountType)?> GetAccountStatusAsync(WarpAccountInfo acc)
    {
        if (string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token)) return null;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {acc.Token}");
            client.DefaultRequestHeaders.Add("CF-Client-Version", "a-6.30");

            var response = await client.GetAsync($"https://api.cloudflareclient.com/v0a1922/reg/{acc.Id}/account");
            if (!response.IsSuccessStatusCode) return null;

            var jsonResp = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResp);
            var root = doc.RootElement.TryGetProperty("result", out var resEl) ? resEl : doc.RootElement;

            // "warp_plus" LUÔN trả true bất kể tier thật (xác nhận qua test thực tế:
            // account_type "free" nhưng warp_plus vẫn true) — field đáng tin để biết
            // tier thật là "account_type" ("free" / "limited" / "unlimited", chỉ
            // "unlimited" mới là WARP+ thật).
            string accountType = root.TryGetProperty("account_type", out var atEl) ? (atEl.GetString() ?? "") : "";
            bool warpPlus = accountType.Equals("unlimited", StringComparison.OrdinalIgnoreCase);
            return (warpPlus, accountType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Đặt tên hiển thị của thiết bị trên Cloudflare (hiện trong app 1.1.1.1 →
    /// Settings → Account → danh sách device) — giúp phân biệt slot nào của
    /// máy/engine nào khi tài khoản có nhiều thiết bị dùng chung 1 license.
    /// </summary>
    private static async Task<bool> TrySetDeviceNameAsync(HttpClient client, string id, string token, string apiVersion, string name)
    {
        try
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var payload = new { name };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PatchAsync($"https://api.cloudflareclient.com/{apiVersion}/reg/{id}", content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đặt tên thiết bị WireGuard 1 lần duy nhất (dạng "{tên máy} - WireGuard")
    /// để phân biệt trong danh sách device 1.1.1.1 khi tài khoản có nhiều máy/
    /// nhiều engine dùng chung license — không gọi lại nếu đã đặt thành công.
    /// </summary>
    private static async Task EnsureDeviceNamedAsync(WarpAccountInfo acc)
    {
        if (acc.DeviceNameSet || string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token)) return;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");
            var ok = await TrySetDeviceNameAsync(client, acc.Id, acc.Token, "v0a1922",
                $"{Environment.MachineName} - WireGuard (AOW Booster)");
            if (ok)
            {
                acc.DeviceNameSet = true;
                var json = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(AccountFilePath, json);
            }
        }
        catch { }
    }

    /// <summary>Tương tự <see cref="EnsureDeviceNamedAsync(WarpAccountInfo)"/> nhưng cho tài khoản MASQUE riêng.</summary>
    private static async Task EnsureMasqueDeviceNamedAsync(WarpMasqueAccountInfo acc)
    {
        if (acc.DeviceNameSet || string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token)) return;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");
            var ok = await TrySetDeviceNameAsync(client, acc.Id, acc.Token, MasqueApiVersion,
                $"{Environment.MachineName} - MASQUE Beta (AOW Booster)");
            if (ok)
            {
                acc.DeviceNameSet = true;
                var json = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(MasqueAccountFilePath, json);
            }
        }
        catch { }
    }

    private static bool IsZeroClientId(string clientId)
    {
        try
        {
            var bytes = Convert.FromBase64String(clientId);
            return bytes.Length == 0 || bytes.All(b => b == 0);
        }
        catch
        {
            return true; // Không decode được thì coi như không hợp lệ, cần vá lại.
        }
    }

    /// <summary>
    /// Hỏi thẳng Cloudflare client_id (3 byte "reserved" thật) đã cấp cho thiết
    /// bị <paramref name="id"/> — dùng để vá lại các tài khoản cũ bị lưu sai
    /// (mặc định zero) từ luồng đăng ký qua wgcf.
    /// </summary>
    private static async Task<string?> FetchClientIdAsync(string id, string token)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Add("CF-Client-Version", "a-6.30");

            var response = await client.GetAsync($"https://api.cloudflareclient.com/v0a1922/reg/{id}");
            if (!response.IsSuccessStatusCode) return null;

            var jsonResp = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResp);
            var root = doc.RootElement.TryGetProperty("result", out var resEl) ? resEl : doc.RootElement;

            if (root.TryGetProperty("config", out var configEl) &&
                configEl.TryGetProperty("client_id", out var clientIdEl))
            {
                return clientIdEl.GetString();
            }
        }
        catch { }
        return null;
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
        var response = await client.PostAsync("https://api.cloudflareclient.com/v0a1922/reg", content);
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

            if (configEl.TryGetProperty("client_id", out var clientIdEl))
            {
                result.ClientId = clientIdEl.GetString() ?? "";
            }
        }

        if (string.IsNullOrEmpty(result.IPv4)) result.IPv4 = "172.16.0.2";

        // Kích hoạt WARP cho thiết bị vừa đăng ký — thiếu bước này thì Cloudflare
        // vẫn trả về config hợp lệ nhưng sẽ âm thầm bỏ qua handshake WireGuard
        // (peer coi như chưa "enabled" ở tầng edge).
        if (!string.IsNullOrEmpty(result.Id) && !string.IsNullOrEmpty(result.Token))
        {
            try
            {
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
                var enablePayload = new { warp_enabled = true };
                var enableContent = new StringContent(
                    JsonSerializer.Serialize(enablePayload), Encoding.UTF8, "application/json");
                await client.PatchAsync(
                    $"https://api.cloudflareclient.com/v0a1922/reg/{result.Id}", enableContent);
            }
            catch { }
        }

        return result;
    }

    // ── Đăng ký tài khoản WARP tự động qua wgcf (ViRb3/wgcf) ──────
    // Tài khoản tự đăng ký thẳng qua API Cloudflare (RegisterNewWarpAccountAsync)
    // bị Cloudflare xếp vào nhóm chính sách MASQUE, khiến WireGuard cổ điển
    // (mihomo) không handshake được. wgcf hiện vẫn được cấp WireGuard cổ điển,
    // nên app tự gọi wgcf.exe (nhúng sẵn) để đăng ký thay cho việc tự gọi API.
    private static async Task<WarpAccountInfo?> RegisterViaWgcfAsync()
    {
        try
        {
            if (!EnsureWgcfExtracted()) return null;

            var workDir = Path.Combine(CoreDir, "wgcf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                if (!await RunWgcfAsync(workDir, "register --accept-tos")) return null;
                if (!await RunWgcfAsync(workDir, "generate")) return null;

                var accountTomlPath = Path.Combine(workDir, "wgcf-account.toml");
                var profileConfPath = Path.Combine(workDir, "wgcf-profile.conf");
                if (!File.Exists(accountTomlPath) || !File.Exists(profileConfPath)) return null;

                var result = ParseWgcfFiles(
                    await File.ReadAllTextAsync(accountTomlPath),
                    await File.ReadAllTextAsync(profileConfPath));

                // wg-quick/wgcf không ghi client_id vào file — hỏi thẳng Cloudflare
                // để lấy đúng 3 byte "reserved" thật của thiết bị vừa đăng ký, thay
                // vì để zero (Cloudflare dùng byte này gắn chính sách QoS/WARP+ ở
                // tầng edge cho từng session, độc lập với việc handshake thành công).
                if (!string.IsNullOrEmpty(result.Id) && !string.IsNullOrEmpty(result.Token))
                {
                    var realClientId = await FetchClientIdAsync(result.Id, result.Token);
                    if (!string.IsNullOrEmpty(realClientId)) result.ClientId = realClientId;
                }

                return result;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool EnsureWgcfExtracted()
    {
        try
        {
            if (File.Exists(WgcfExePath)) return true;
            if (!Directory.Exists(CoreDir)) Directory.CreateDirectory(CoreDir);

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith(".wgcf.exe", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return false;

            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream == null) return false;

            using var fileStream = File.Create(WgcfExePath);
            stream.CopyTo(fileStream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunWgcfAsync(string workDir, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WgcfExePath,
            Arguments = arguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return false;

        var waitTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != waitTask)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        return proc.ExitCode == 0;
    }

    // ── Import tài khoản wgcf có sẵn (Import Account thủ công) ───
    public static async Task<(bool Success, string Message)> ImportFromWgcfFilesAsync(
        string accountTomlContent, string profileConfContent)
    {
        try
        {
            var info = ParseWgcfFiles(accountTomlContent, profileConfContent);
            if (string.IsNullOrEmpty(info.PrivateKey) || string.IsNullOrEmpty(info.IPv4))
                return (false, "File không hợp lệ — thiếu PrivateKey hoặc Address.");

            var dir = Path.GetDirectoryName(AccountFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(AccountFilePath, json);

            return (true, "Import tài khoản thành công!");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi khi đọc file: {ex.Message}");
        }
    }

    private static WarpAccountInfo ParseWgcfFiles(string accountToml, string profileConf)
    {
        var toml = ParseSimpleToml(accountToml);
        var result = new WarpAccountInfo
        {
            PrivateKey = toml.GetValueOrDefault("private_key", ""),
            Id         = toml.GetValueOrDefault("device_id", ""),
            Token      = toml.GetValueOrDefault("access_token", ""),
            License    = toml.GetValueOrDefault("license_key", ""),
            // Placeholder — RegisterViaWgcfAsync sẽ gọi FetchClientIdAsync() ngay
            // sau đây để lấy đúng client_id thật từ Cloudflare, ghi đè giá trị này.
            ClientId   = "AAAA"
        };

        foreach (var rawLine in profileConf.Split('\n'))
        {
            var line = rawLine.Trim();
            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;
            var key = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim();

            if (key.Equals("Address", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in value.Split(','))
                {
                    var ip = part.Trim().Split('/')[0];
                    if (ip.Contains(':')) result.IPv6 = ip;
                    else if (!string.IsNullOrEmpty(ip)) result.IPv4 = ip;
                }
            }
            else if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
            {
                result.PeerPublicKey = value;
            }
            else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                result.Endpoint = value;
            }
        }

        if (string.IsNullOrEmpty(result.IPv4)) result.IPv4 = "172.16.0.2";
        return result;
    }

    private static Dictionary<string, string> ParseSimpleToml(string content)
    {
        var dict = new Dictionary<string, string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;
            var key = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim().Trim('\'', '"');
            dict[key] = value;
        }
        return dict;
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

            var url = $"https://api.cloudflareclient.com/v0a1922/reg/{acc.Id}/account";
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

    // ══════════════════════════════════════════════════════════
    // Direct MASQUE (Beta) — đăng ký & tài khoản HOÀN TOÀN riêng
    // ══════════════════════════════════════════════════════════
    private static readonly string MasqueAccountFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "warp_masque_account.json");

    // Endpoint/header đúng theo cách usque (nguồn mihomo fork MASQUE từ đây)
    // đăng ký thiết bị + xin key MASQUE — KHÁC endpoint v0a1922 dùng cho
    // WireGuard classic (wgcf) để tránh Cloudflare gộp chung chính sách.
    private const string MasqueApiVersion       = "v0a4471";
    private const string MasqueClientVersionHdr = "a-6.35-4471";

    public static async Task<WarpMasqueAccountInfo> GetOrCreateMasqueAccountAsync()
    {
        if (File.Exists(MasqueAccountFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(MasqueAccountFilePath);
                var acc = JsonSerializer.Deserialize<WarpMasqueAccountInfo>(json);
                if (acc != null && !string.IsNullOrEmpty(acc.PrivateKey) &&
                    !string.IsNullOrEmpty(acc.PeerPublicKey) && !string.IsNullOrEmpty(acc.IPv4))
                {
                    await EnsureMasqueDeviceNamedAsync(acc);
                    return acc;
                }
            }
            catch { }
        }

        var newAcc = await RegisterMasqueAccountAsync();

        try
        {
            var dir = Path.GetDirectoryName(MasqueAccountFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(newAcc, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(MasqueAccountFilePath, json);
        }
        catch { }

        await EnsureMasqueDeviceNamedAsync(newAcc);
        return newAcc;
    }

    private static async Task<WarpMasqueAccountInfo> RegisterMasqueAccountAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");

        // 1. Đăng ký thiết bị mới (giống flow WireGuard raw API, nhưng đây là
        //    thiết bị RIÊNG, key gửi lên ban đầu không dùng — sẽ ghi đè bằng
        //    key ECDSA ở bước PATCH tiếp theo).
        using var ecdsaTemp = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tempPubKeyB64 = Convert.ToBase64String(ecdsaTemp.ExportSubjectPublicKeyInfo());

        var regPayload = new
        {
            install_id = "",
            fcm_token = "",
            tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            key = tempPubKeyB64,
            type = "Android",
            locale = "en_US"
        };
        var regContent = new StringContent(JsonSerializer.Serialize(regPayload), Encoding.UTF8, "application/json");
        var regResponse = await client.PostAsync($"https://api.cloudflareclient.com/{MasqueApiVersion}/reg", regContent);
        regResponse.EnsureSuccessStatusCode();

        var regJson = await regResponse.Content.ReadAsStringAsync();
        using var regDoc = JsonDocument.Parse(regJson);
        var regRoot = regDoc.RootElement.TryGetProperty("result", out var regResEl) ? regResEl : regDoc.RootElement;

        var result = new WarpMasqueAccountInfo();
        if (regRoot.TryGetProperty("id", out var idEl)) result.Id = idEl.GetString() ?? "";
        if (regRoot.TryGetProperty("token", out var tokenEl)) result.Token = tokenEl.GetString() ?? "";

        if (string.IsNullOrEmpty(result.Id) || string.IsNullOrEmpty(result.Token))
            throw new Exception("Đăng ký thiết bị MASQUE thất bại — không nhận được id/token từ Cloudflare.");

        // 2. Sinh key ECDSA P-256 thật, gửi PATCH xin chuyển thiết bị này sang
        //    chính sách MASQUE (key_type: secp256r1, tunnel_type: masque).
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // mihomo parse private-key bằng x509.ParseECPrivateKey (SEC1/RFC5915),
        // KHÔNG phải PKCS8 — ExportPkcs8PrivateKey() sai định dạng, lỗi:
        // "failed to parse private key (use ParsePKCS8PrivateKey instead)".
        var privKeyDer = ecdsa.ExportECPrivateKey();
        var pubKeyDer  = ecdsa.ExportSubjectPublicKeyInfo();
        result.PrivateKey = Convert.ToBase64String(privKeyDer);

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
        client.DefaultRequestHeaders.Remove("CF-Client-Version");
        client.DefaultRequestHeaders.Add("CF-Client-Version", MasqueClientVersionHdr);

        var keyPayload = new
        {
            key = Convert.ToBase64String(pubKeyDer),
            key_type = "secp256r1",
            tunnel_type = "masque"
        };
        var keyContent = new StringContent(JsonSerializer.Serialize(keyPayload), Encoding.UTF8, "application/json");
        var keyResponse = await client.PatchAsync(
            $"https://api.cloudflareclient.com/{MasqueApiVersion}/reg/{result.Id}", keyContent);
        keyResponse.EnsureSuccessStatusCode();

        // 3. Kích hoạt WARP cho thiết bị (giống bước bắt buộc ở flow WireGuard).
        var enablePayload = new { warp_enabled = true };
        var enableContent = new StringContent(JsonSerializer.Serialize(enablePayload), Encoding.UTF8, "application/json");
        await client.PatchAsync($"https://api.cloudflareclient.com/{MasqueApiVersion}/reg/{result.Id}", enableContent);

        // 4. Đọc lại config đầy đủ để lấy IP nội bộ + public key của relay
        //    Cloudflare (peer) — KHÔNG phải hardcode, mỗi thiết bị/kết nối có
        //    thể khác, phải lấy đúng từ response.
        await RefreshMasqueConfigAsync(client, result);

        return result;
    }

    /// <summary>
    /// GET lại config đầy đủ của thiết bị MASQUE và ghi đè IPv4/IPv6/PeerPublicKey
    /// vào <paramref name="acc"/>. Cloudflare có thể cấp lại peer/relay khác khi
    /// đổi tier (áp WARP+) — bắt buộc gọi lại sau UpdateMasqueLicenseAsync, nếu
    /// không cert-pinning (public-key) cũ sẽ lệch, mihomo báo "login failed...
    /// tls key and cert is not enrolled".
    /// </summary>
    private static async Task RefreshMasqueConfigAsync(HttpClient client, WarpMasqueAccountInfo acc)
    {
        var fullResponse = await client.GetAsync($"https://api.cloudflareclient.com/{MasqueApiVersion}/reg/{acc.Id}");
        fullResponse.EnsureSuccessStatusCode();
        var fullJson = await fullResponse.Content.ReadAsStringAsync();
        using var fullDoc = JsonDocument.Parse(fullJson);
        var fullRoot = fullDoc.RootElement.TryGetProperty("result", out var fullResEl) ? fullResEl : fullDoc.RootElement;

        if (fullRoot.TryGetProperty("config", out var configEl))
        {
            if (configEl.TryGetProperty("interface", out var ifaceEl) &&
                ifaceEl.TryGetProperty("addresses", out var addrEl))
            {
                if (addrEl.TryGetProperty("v4", out var v4El)) acc.IPv4 = v4El.GetString() ?? acc.IPv4;
                if (addrEl.TryGetProperty("v6", out var v6El)) acc.IPv6 = v6El.GetString() ?? acc.IPv6;
            }

            if (configEl.TryGetProperty("peers", out var peersEl) &&
                peersEl.ValueKind == JsonValueKind.Array && peersEl.GetArrayLength() > 0)
            {
                var peer = peersEl[0];
                if (peer.TryGetProperty("public_key", out var peerPkEl))
                    acc.PeerPublicKey = NormalizePemToBase64(peerPkEl.GetString() ?? "");
            }
        }

        if (string.IsNullOrEmpty(acc.PeerPublicKey))
            throw new Exception("Không lấy được public key của Cloudflare relay cho MASQUE — có thể tài khoản chưa được cấp chính sách MASQUE.");
        if (string.IsNullOrEmpty(acc.IPv4))
            acc.IPv4 = "172.16.0.2";
    }

    /// <summary>
    /// Cloudflare trả public_key ở dạng PEM nhiều dòng
    /// ("-----BEGIN PUBLIC KEY-----\n...\n-----END..."), không phải base64
    /// thuần 1 dòng — nếu ghi thẳng vào YAML sẽ vỡ parser (newline giữa
    /// scalar không escape). Bóc header/footer + nối các dòng lại.
    /// </summary>
    private static string NormalizePemToBase64(string value)
    {
        if (!value.Contains("BEGIN")) return value.Trim();

        var lines = value.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("-----"));
        return string.Concat(lines);
    }

    /// <summary>
    /// Cập nhật WARP+ license cho tài khoản MASQUE — thiết bị đăng ký RIÊNG
    /// (id/token khác WireGuard), nên áp license ở đây sẽ tốn thêm 1/5 slot
    /// thiết bị WARP+ của bạn, độc lập với slot của Direct WireGuard.
    /// </summary>
    public static async Task<(bool Success, string Message)> UpdateMasqueLicenseAsync(string licenseKey)
    {
        try
        {
            var acc = await GetOrCreateMasqueAccountAsync();
            if (string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token))
                return (false, "Không tìm thấy ID hoặc Token tài khoản MASQUE. Vui lòng xóa file warp_masque_account.json và khởi động lại app.");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "okhttp/3.12.1");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {acc.Token}");
            client.DefaultRequestHeaders.Add("CF-Client-Version", MasqueClientVersionHdr);

            var payload = new { license = licenseKey };
            var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var url = $"https://api.cloudflareclient.com/{MasqueApiVersion}/reg/{acc.Id}/account";
            var response = await client.PutAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                acc.License = licenseKey;

                // Cloudflare có thể cấp lại peer/relay khác khi đổi tier — refetch
                // config, không thì cert-pinning cũ lệch, tunnel báo "login failed".
                try { await RefreshMasqueConfigAsync(client, acc); } catch { }

                var dir = Path.GetDirectoryName(MasqueAccountFilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(MasqueAccountFilePath, json);
                return (true, "Kích hoạt WARP+ cho MASQUE thành công!");
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

    /// <summary>Hỏi thẳng Cloudflare tài khoản MASQUE đã là WARP+ hay chưa.</summary>
    public static async Task<(bool WarpPlus, string AccountType)?> GetMasqueAccountStatusAsync(WarpMasqueAccountInfo acc)
    {
        if (string.IsNullOrEmpty(acc.Id) || string.IsNullOrEmpty(acc.Token)) return null;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {acc.Token}");
            client.DefaultRequestHeaders.Add("CF-Client-Version", MasqueClientVersionHdr);

            var response = await client.GetAsync($"https://api.cloudflareclient.com/{MasqueApiVersion}/reg/{acc.Id}/account");
            if (!response.IsSuccessStatusCode) return null;

            var jsonResp = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResp);
            var root = doc.RootElement.TryGetProperty("result", out var resEl) ? resEl : doc.RootElement;

            // "warp_plus" LUÔN trả true bất kể tier thật (xác nhận qua test thực tế:
            // account_type "free" nhưng warp_plus vẫn true) — field đáng tin để biết
            // tier thật là "account_type" ("free" / "limited" / "unlimited", chỉ
            // "unlimited" mới là WARP+ thật).
            string accountType = root.TryGetProperty("account_type", out var atEl) ? (atEl.GetString() ?? "") : "";
            bool warpPlus = accountType.Equals("unlimited", StringComparison.OrdinalIgnoreCase);
            return (warpPlus, accountType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Xoá tài khoản MASQUE riêng — không ảnh hưởng tài khoản WireGuard.</summary>
    public static Task<(bool Success, string Message)> ResetMasqueAccountAsync()
    {
        try
        {
            if (File.Exists(MasqueAccountFilePath))
                File.Delete(MasqueAccountFilePath);
            return Task.FromResult((true, "Đã xóa tài khoản MASQUE. Sẽ tự đăng ký lại khi Boost lần sau."));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, $"Không thể xóa file tài khoản MASQUE: {ex.Message}"));
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
