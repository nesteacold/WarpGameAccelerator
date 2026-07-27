// ============================================================
// Services/BoostStateService.cs
// Lưu/khôi phục trạng thái Boost gần nhất (profile/process, đang
// kết nối hay không) để phục hồi sau khi app bị crash/khởi động lại.
// ============================================================
using System.IO;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class BoostStateInfo
{
    public bool   WasConnected  { get; set; }
    public string ProcessName   { get; set; } = string.Empty;
    public string ProfileName   { get; set; } = string.Empty;
    public string SavedAt       { get; set; } = string.Empty;
}

public static class BoostStateService
{
    private static readonly string StateFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "boost_state.json");

    public static void SaveState(bool wasConnected, string processName, string profileName)
    {
        try
        {
            var info = new BoostStateInfo
            {
                WasConnected = wasConnected,
                ProcessName  = processName,
                ProfileName  = profileName,
                SavedAt      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var dir = Path.GetDirectoryName(StateFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(info));
        }
        catch { }
    }

    public static BoostStateInfo? LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return null;
            return JsonSerializer.Deserialize<BoostStateInfo>(File.ReadAllText(StateFilePath));
        }
        catch { return null; }
    }
}
