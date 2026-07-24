// ============================================================
// Services/MultiClientService.cs
// Quản lý việc mở nhiều client game AOW (Age of Wushu)
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class AowTokenInfo
{
    public string Token       { get; set; } = string.Empty;
    public string GameFolder  { get; set; } = string.Empty;
    public string SavedAt     { get; set; } = string.Empty;
}

public class RunningClient
{
    public int    Pid       { get; set; }
    public string StartTime { get; set; } = string.Empty;
}

public class MultiClientService
{
    private static readonly string TokenFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "aow_token.json");

    // ── Validate thư mục game ────────────────────────────────
    public static (bool Valid, string Message) ValidateGameFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return (false, "Thư mục không tồn tại.");

        string? launcher = FindGameExe(folder, "fxlaunch.exe");
        string? game     = FindGameExe(folder, "fxgame.exe");

        if (launcher == null) return (false, "Không tìm thấy fxlaunch.exe.");
        if (game == null)     return (false, "Không tìm thấy fxgame.exe.");

        return (true, $"✓  fxlaunch.exe  ·  fxgame.exe");
    }

    public static string? FindGameExe(string folder, string exeName)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        // 1. Kiểm tra trực tiếp tại thư mục gốc và các subfolder quen thuộc
        var searchDirs = new[] { folder, Path.Combine(folder, "bin64"), Path.Combine(folder, "Bin64"), Path.Combine(folder, "bin"), Path.Combine(folder, "Bin") };
        foreach (var dir in searchDirs)
        {
            if (Directory.Exists(dir))
            {
                var path = Path.Combine(dir, exeName);
                if (File.Exists(path)) return path;
            }
        }

        // 2. Nếu không thấy, quét đệ quy subfolder
        try
        {
            var files = Directory.GetFiles(folder, exeName, SearchOption.AllDirectories);
            if (files.Length > 0) return files[0];
        }
        catch { }

        return null;
    }

    // ── Bước 1: Mở client đầu tiên qua fxlaunch ──────────
    public static Task<(bool Success, string Message)> LaunchFirstClientAsync(string gameFolder)
    {
        try
        {
            var launcher = FindGameExe(gameFolder, "fxlaunch.exe");
            if (launcher == null)
                return Task.FromResult((false, "Không tìm thấy fxlaunch.exe trong thư mục đã chọn."));

            var psi = new ProcessStartInfo
            {
                FileName         = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
                UseShellExecute  = true
            };
            Process.Start(psi);
            return Task.FromResult((true, "Đã gọi fxlaunch.exe. Chờ vài giây để game khởi động..."));
        }        catch (Exception ex)
        {
            return Task.FromResult((false, $"Lỗi: {ex.Message}"));
        }
    }

    // ── Bước 2: Detect token từ fxgame.exe đang chạy ────────
    public static Task<(bool Success, string Token, string Message)> DetectTokenAsync()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'fxgame.exe'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var cmdLine = obj["CommandLine"]?.ToString();
                if (string.IsNullOrEmpty(cmdLine)) continue;

                // CommandLine: "C:\...\fxgame.exe" TOKEN
                // Tách token ra khỏi phần exe path
                string token = ParseTokenFromCommandLine(cmdLine);
                if (!string.IsNullOrEmpty(token))
                    return Task.FromResult((true, token, "Detect token thành công!"));
            }

            return Task.FromResult((false, string.Empty,
                "Không tìm thấy fxgame.exe đang chạy. Hãy mở client đầu tiên trước."));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, string.Empty, $"Lỗi WMI: {ex.Message}"));
        }
    }

    private static string ParseTokenFromCommandLine(string cmdLine)
    {
        // Chuỗi dạng: "C:\Path\fxgame.exe" TOKEN
        // hoặc: C:\Path\fxgame.exe TOKEN
        cmdLine = cmdLine.Trim();
        string rest;

        if (cmdLine.StartsWith("\""))
        {
            // Có ngoặc kép → tìm ngoặc đóng
            int closing = cmdLine.IndexOf('"', 1);
            if (closing < 0) return string.Empty;
            rest = cmdLine[(closing + 1)..].Trim();
        }
        else
        {
            // Không có ngoặc → tách theo khoảng trắng đầu tiên
            int space = cmdLine.IndexOf(' ');
            if (space < 0) return string.Empty;
            rest = cmdLine[(space + 1)..].Trim();
        }

        // rest là token (hoặc có thêm args khác phía sau, lấy word đầu tiên)
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    // ── Lưu / Đọc token ──────────────────────────────────────
    public static async Task SaveTokenAsync(string token, string gameFolder)
    {
        var info = new AowTokenInfo
        {
            Token      = token,
            GameFolder = gameFolder,
            SavedAt    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        var dir = Path.GetDirectoryName(TokenFilePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(TokenFilePath,
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AowTokenInfo? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenFilePath)) return null;
            var json = File.ReadAllText(TokenFilePath);
            return JsonSerializer.Deserialize<AowTokenInfo>(json);
        }
        catch { return null; }
    }

    public static void DeleteToken()
    {
        if (File.Exists(TokenFilePath)) File.Delete(TokenFilePath);
    }

    // ── Bước 3: Mở thêm client với token đã lưu ─────────────
    public static async Task<(int Launched, string Message)> LaunchAdditionalClientsAsync(
        string gameFolder, string token, int count)
    {
        var gamePath = FindGameExe(gameFolder, "fxgame.exe");
        if (gamePath == null)
            return (0, "Không tìm thấy fxgame.exe.");

        int launched = 0;
        var errors   = new List<string>();

        for (int i = 0; i < count; i++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName         = gamePath,
                    Arguments        = token,
                    WorkingDirectory = Path.GetDirectoryName(gamePath)!,
                    UseShellExecute  = true
                };
                Process.Start(psi);
                launched++;
                await Task.Delay(800); // Delay nhỏ giữa các lần mở tránh flood
            }
            catch (Exception ex)
            {
                errors.Add($"Client {i + 1}: {ex.Message}");
            }
        }

        string msg = launched == count
            ? $"Đã mở {launched} client thành công!"
            : $"Mở được {launched}/{count} client. Lỗi: {string.Join(", ", errors)}";

        return (launched, msg);
    }

    // ── Quản lý client đang chạy ─────────────────────────────
    public static List<RunningClient> GetRunningClients()
    {
        var result = new List<RunningClient>();
        try
        {
            foreach (var p in Process.GetProcessesByName("fxgame"))
            {
                result.Add(new RunningClient
                {
                    Pid       = p.Id,
                    StartTime = p.StartTime.ToString("HH:mm:ss")
                });
            }
        }
        catch { }
        return result;
    }

    public static bool KillClient(int pid)
    {
        try
        {
            Process.GetProcessById(pid).Kill();
            return true;
        }
        catch { return false; }
    }
}
