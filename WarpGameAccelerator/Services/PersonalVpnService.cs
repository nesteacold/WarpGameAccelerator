// ============================================================
// Services/PersonalVpnService.cs — Kênh VPN cá nhân (dual-tunnel, Dev Panel)
// Hoàn toàn độc lập WarpAccountInfo/WarpMasqueAccountInfo — đây là config
// WireGuard server RIÊNG của người dùng (không đăng ký qua Cloudflare).
//
// Multi-profile (Phase 2): giống WireGuard client chính thức — import nhiều
// file .conf, mỗi file thành 1 profile, chỉ 1 profile được chọn Active tại
// một thời điểm. "IsActive" (kênh cá nhân đang Boost hay không) tách biệt
// khỏi việc profile nào đang được chọn.
// ============================================================
using System.IO;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class PersonalVpnProfile
{
    public string Id            { get; set; } = Guid.NewGuid().ToString("N");
    public string Name          { get; set; } = string.Empty;
    public string PrivateKey    { get; set; } = string.Empty;
    public string AddressV4     { get; set; } = string.Empty;
    public string AddressV6     { get; set; } = string.Empty;
    public string Dns           { get; set; } = string.Empty;
    public string PeerPublicKey { get; set; } = string.Empty;
    public string PresharedKey  { get; set; } = string.Empty;
    public string Endpoint      { get; set; } = string.Empty;
    public string AllowedIPs    { get; set; } = "0.0.0.0/0";
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>
    /// Tên service "WireGuardTunnel$*" trên MÁY NÀY tuyệt đối KHÔNG bị
    /// WireGuardConflictGuard tạm dừng khi Boost — dùng khi chính máy này
    /// vừa Boost game vừa đóng vai WireGuard server cho kênh cá nhân (2 vai
    /// trò trùng 1 máy). Người dùng thật thường có server ở máy khác, không
    /// cần field này. Tự chịu rủi ro xung đột WFP đã ghi ở CLAUDE.md nếu bật.
    /// </summary>
    public string ExcludedTunnelServiceName { get; set; } = string.Empty;
}

public class PersonalVpnStore
{
    public List<PersonalVpnProfile> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }

    /// <summary>Trạng thái Boost của kênh cá nhân — độc lập với kênh game.</summary>
    public bool IsActive { get; set; } = false;
}

/// <summary>
/// Import/lưu nhiều profile WireGuard cá nhân (wg-quick chuẩn) + danh sách
/// process route qua kênh này. Static-style giống WarpAccountService.
/// </summary>
public static class PersonalVpnService
{
    private static readonly string ConfigFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "personal_vpn.json");

    public static PersonalVpnStore GetStore()
    {
        if (!File.Exists(ConfigFilePath)) return new PersonalVpnStore();

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<PersonalVpnStore>(json) ?? new PersonalVpnStore();
        }
        catch
        {
            // Schema cũ (single-profile, Phase 1) không tương thích — coi như
            // chưa có dữ liệu, người dùng import lại .conf.
            return new PersonalVpnStore();
        }
    }

    public static PersonalVpnProfile? GetActiveProfile()
    {
        var store = GetStore();
        if (string.IsNullOrEmpty(store.ActiveProfileId)) return null;
        return store.Profiles.FirstOrDefault(p => p.Id == store.ActiveProfileId);
    }

    public static bool IsChannelActive() => GetStore().IsActive;

    /// <summary>
    /// Config hợp lệ để dùng cho outbound mihomo khi profile Active có đủ
    /// PrivateKey/PeerPublicKey/Endpoint/AddressV4 và kênh đang Active.
    /// </summary>
    public static bool TryGetActiveValidConfig(out PersonalVpnProfile? profile)
    {
        var store = GetStore();
        profile = null;
        if (!store.IsActive) return false;

        var active = string.IsNullOrEmpty(store.ActiveProfileId)
            ? null
            : store.Profiles.FirstOrDefault(p => p.Id == store.ActiveProfileId);
        if (active == null) return false;

        bool valid = !string.IsNullOrWhiteSpace(active.PrivateKey)
            && !string.IsNullOrWhiteSpace(active.PeerPublicKey)
            && !string.IsNullOrWhiteSpace(active.Endpoint)
            && !string.IsNullOrWhiteSpace(active.AddressV4)
            && active.ProcessNames.Count > 0;

        if (!valid) return false;
        profile = active;
        return true;
    }

    /// <summary>
    /// Parse file .conf chuẩn wg-quick — CÓ nhận diện section [Interface]/
    /// [Peer] (khác ParseWgcfFiles trong WarpAccountService, chỉ match key
    /// toàn cục vì PrivateKey ở đó lấy từ file TOML riêng, không từ .conf).
    /// Thêm 1 profile MỚI vào store (không overwrite profile đã có).
    /// </summary>
    public static (bool Success, string Message) ImportConfig(string confContent, string? displayName = null)
    {
        try
        {
            var profile = new PersonalVpnProfile();
            string currentSection = "";

            foreach (var rawLine in confContent.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line.Trim('[', ']').Trim().ToLowerInvariant();
                    continue;
                }

                var eqIdx = line.IndexOf('=');
                if (eqIdx < 0) continue;
                var key = line[..eqIdx].Trim();
                var value = line[(eqIdx + 1)..].Trim();

                if (currentSection == "interface")
                {
                    if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
                        profile.PrivateKey = value;
                    else if (key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var part in value.Split(','))
                        {
                            // Bỏ CIDR suffix ("/32") — field "ip:" của mihomo chỉ nhận IP thuần.
                            var ip = part.Trim().Split('/')[0];
                            if (ip.Contains(':')) profile.AddressV6 = ip;
                            else if (ip.Length > 0) profile.AddressV4 = ip;
                        }
                    }
                    else if (key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
                        profile.Dns = value;
                }
                else if (currentSection == "peer")
                {
                    if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
                        profile.PeerPublicKey = value;
                    else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase))
                        profile.PresharedKey = value;
                    else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                        profile.Endpoint = value;
                    else if (key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                        profile.AllowedIPs = value;
                }
            }

            if (string.IsNullOrEmpty(profile.PrivateKey) || string.IsNullOrEmpty(profile.AddressV4))
                return (false, "File .conf không hợp lệ — thiếu PrivateKey hoặc Address ở [Interface].");
            if (string.IsNullOrEmpty(profile.PeerPublicKey) || string.IsNullOrEmpty(profile.Endpoint))
                return (false, "File .conf không hợp lệ — thiếu PublicKey hoặc Endpoint ở [Peer].");

            profile.Name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName!.Trim()
                : profile.Endpoint.Split(':')[0];

            var store = GetStore();
            store.Profiles.Add(profile);
            store.ActiveProfileId = profile.Id;
            SaveStore(store);
            return (true, $"Import profile \"{profile.Name}\" thành công!");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi khi đọc file: {ex.Message}");
        }
    }

    public static void SetActiveProfile(string profileId)
    {
        var store = GetStore();
        if (!store.Profiles.Any(p => p.Id == profileId)) return;

        store.ActiveProfileId = profileId;
        SaveStore(store);
    }

    public static void DeleteProfile(string profileId)
    {
        var store = GetStore();
        store.Profiles.RemoveAll(p => p.Id == profileId);
        if (store.ActiveProfileId == profileId)
            store.ActiveProfileId = store.Profiles.FirstOrDefault()?.Id;
        SaveStore(store);
    }

    public static void SaveSelectedProcesses(string profileId, List<string> processNames)
    {
        var store = GetStore();
        var profile = store.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        profile.ProcessNames = processNames;
        SaveStore(store);
    }

    public static void SetActive(bool active)
    {
        var store = GetStore();
        store.IsActive = active;
        SaveStore(store);
    }

    public static void SetExcludedTunnelServiceName(string profileId, string serviceName)
    {
        var store = GetStore();
        var profile = store.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        profile.ExcludedTunnelServiceName = serviceName.Trim();
        SaveStore(store);
    }

    private static void SaveStore(PersonalVpnStore store)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json);
        }
        catch { }
    }
}
