// ============================================================
// Services/CrashReportService.cs
// Dịch vụ ghi nhận & gửi báo cáo crash log tự động lên GitHub
// ============================================================
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class CrashReportInfo
{
    public string AppVersion   { get; set; } = string.Empty;
    public string OsVersion    { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string Message       { get; set; } = string.Empty;
    public string StackTrace    { get; set; } = string.Empty;
    public string Timestamp    { get; set; } = string.Empty;
}

public class CrashReportService
{
    private static readonly string CrashPendingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Logs", "crash_pending.json");

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Logs", "crash.log");

    // ── Ghi vết crash khi xảy ra ngoại lệ unhandled ────────────
    public static void RecordCrash(Exception ex, string source)
    {
        try
        {
            var dir = Path.GetDirectoryName(CrashPendingPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Ghi vào file log nối tiếp crash.log
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(CrashLogPath, logEntry);

            // Ghi vào file crash_pending.json để hỏi gửi ở lần bật app sau
            var info = new CrashReportInfo
            {
                AppVersion    = GetAppVersion(),
                OsVersion     = Environment.OSVersion.ToString(),
                ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                Message       = ex.Message,
                StackTrace    = ex.StackTrace ?? string.Empty,
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CrashPendingPath, json);
        }
        catch
        {
            // Fail silently — tuyệt đối không để việc ghi log gây crash thêm
        }
    }

    // ── Kiểm tra xem có crash log chưa gửi không ─────────────────
    public static bool HasPendingCrashReport()
    {
        try
        {
            return File.Exists(CrashPendingPath);
        }
        catch { return false; }
    }

    public static CrashReportInfo? GetPendingCrashReport()
    {
        try
        {
            if (!File.Exists(CrashPendingPath)) return null;
            var json = File.ReadAllText(CrashPendingPath);
            return JsonSerializer.Deserialize<CrashReportInfo>(json);
        }
        catch { return null; }
    }

    public static void ClearPendingCrashReport()
    {
        try
        {
            if (File.Exists(CrashPendingPath)) File.Delete(CrashPendingPath);
        }
        catch { }
    }

    // ── Gửi Báo Cáo Lỗi Lên GitHub Issues qua REST API ─────────
    public static async Task<bool> SendCrashReportToGitHubAsync(CrashReportInfo info)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(6); // Timeout ngắn 6s
            client.DefaultRequestHeaders.Add("User-Agent", "WarpGameAccelerator-Telemetry");

            // Tự động tạo Issue trên GitHub Repository
            string issueTitle = $"[Crash Report] v{info.AppVersion} - {info.ExceptionType}";
            string issueBody = $@"### ⚠️ Automated Crash Report

| Thống số | Chi tiết |
|---|---|
| **App Version** | `v{info.AppVersion}` |
| **OS Version** | `{info.OsVersion}` |
| **Thời gian** | `{info.Timestamp}` |
| **Loại Ngoại Lệ** | `{info.ExceptionType}` |

#### 📝 Thông điệp lỗi:
```text
{info.Message}
```

#### 🔍 Stack Trace:
```text
{info.StackTrace}
```
";

            var payload = new
            {
                title = issueTitle,
                body  = issueBody,
                labels = new[] { "bug", "crash-report" }
            };

            // Đăng lên endpoint báo cáo crash công khai của repository
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.github.com/repos/nesteacold/WarpGameAccelerator/issues", content);

            ClearPendingCrashReport();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Nếu mất mạng hoặc API fail, xóa pending để không làm phiền người dùng
            ClearPendingCrashReport();
            return false;
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.9.1";
        }
        catch { return "1.9.1"; }
    }
}
