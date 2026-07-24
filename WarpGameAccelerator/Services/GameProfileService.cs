using System.IO;
using System.Text.Json;
using WarpGameAccelerator.Models;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Quản lý danh sách Game Profile: built-in + custom do người dùng tạo.
/// </summary>
public class GameProfileService
{
    private readonly string _customProfilesPath;
    private readonly List<GameProfile> _allProfiles = [];

    // ── Built-in profiles ────────────────────────────────────────────────────

    private static readonly List<GameProfile> BuiltInProfiles =
    [
        new GameProfile
        {
            Name      = "Age of Wushu",
            IconGlyph = "\uE7FC",   // Controller
            IsCustom  = false,
            Executables =
            [
                "fxlaunch.exe",
                "fxupdate.exe",
                "fxgame.exe"
            ]
        },
        new GameProfile
        {
            Name      = "League of Legends",
            IconGlyph = "\uE7FC",
            IsCustom  = false,
            Executables =
            [
                "LeagueClient.exe",
                "LeagueClientUx.exe",
                "League of Legends.exe"
            ]
        },
        new GameProfile
        {
            Name      = "Valorant",
            IconGlyph = "\uE7FC",
            IsCustom  = false,
            Executables =
            [
                "VALORANT.exe",
                "VALORANT-Win64-Shipping.exe",
                "RiotClientServices.exe"
            ]
        },
        new GameProfile
        {
            Name      = "Counter-Strike 2",
            IconGlyph = "\uE7FC",
            IsCustom  = false,
            Executables =
            [
                "cs2.exe",
                "steam.exe"
            ]
        }
    ];

    // ─────────────────────────────────────────────────────────────────────────

    public GameProfileService()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        _customProfilesPath = Path.Combine(dataDir, "custom_profiles.json");
        Load();
    }

    /// <summary>Toàn bộ profiles (built-in + custom)</summary>
    public IReadOnlyList<GameProfile> All => _allProfiles.AsReadOnly();

    /// <summary>Tìm profile phù hợp với tên exe (null nếu không tìm thấy)</summary>
    public GameProfile? FindByExe(string exeName)
    {
        var fileName = Path.GetFileName(exeName); // hỗ trợ cả full path
        return _allProfiles.FirstOrDefault(p => p.Matches(fileName));
    }

    /// <summary>Tạo một profile "tạm" cho file exe chưa thuộc profile nào</summary>
    public static GameProfile CreateAdHoc(string exePath)
    {
        var fileName = Path.GetFileName(exePath);
        return new GameProfile
        {
            Name        = Path.GetFileNameWithoutExtension(fileName),
            IconGlyph   = "\uE768",  // Play button
            IsCustom    = true,
            Executables = [fileName]
        };
    }

    /// <summary>Thêm custom profile và lưu vào disk</summary>
    public void AddCustom(GameProfile profile)
    {
        profile.IsCustom = true;
        _allProfiles.Add(profile);
        Save();
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        _allProfiles.Clear();
        _allProfiles.AddRange(BuiltInProfiles);

        if (File.Exists(_customProfilesPath))
        {
            try
            {
                var json = File.ReadAllText(_customProfilesPath);
                var customs = JsonSerializer.Deserialize<List<GameProfile>>(json);
                if (customs != null)
                    _allProfiles.AddRange(customs);
            }
            catch { /* bỏ qua nếu file bị hỏng */ }
        }
    }

    private void Save()
    {
        try
        {
            var customs = _allProfiles.Where(p => p.IsCustom).ToList();
            var json = JsonSerializer.Serialize(customs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_customProfilesPath, json);
        }
        catch { }
    }
}
