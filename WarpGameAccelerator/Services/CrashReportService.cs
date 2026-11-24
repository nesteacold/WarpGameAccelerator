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

    public static void ClearPendingCrashReport() => ClearPendingCrashReport(CrashPendingPath);

    public static void ClearPendingCrashReport(string pendingPath)
    {
        try
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
        }
        catch { }
    }

    // ── Gửi báo cáo crash ──────────────────────────────────────
    // Đường cũ POST lên GitHub API mà KHÔNG có token — đã đo: trả 401 "Requires authentication",
    // tức hàm không thể trả true bao giờ. Tệ hơn: ClearPendingCrashReport() chạy VÔ ĐIỀU KIỆN ngay
    // sau PostAsync (và lần nữa trong catch), nên vết crash duy nhất vừa bắt được bị xoá dù gửi hỏng,
    // trong khi dialog vẫn báo "✓ Đã lưu vết lỗi nội bộ".
    // Nay: gửi qua kênh Discord đã cấu hình sẵn (kênh mà BugReportService dùng và đã biết là chạy),
    // và CHỈ xoá pending khi server xác nhận thành công — thất bại thì giữ để hỏi lại lần sau.
    public static async Task<bool> UploadPendingCrashAsync(CrashReportInfo info)
    {
        var endpoint = BugReportService.GetWebhookUrl();
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;   // giữ pending: chưa có kênh gửi thì chưa nên vứt bằng chứng

        return await SubmitAsync(info, endpoint, CrashPendingPath);
    }

    // Tách mạng ra khỏi quyết định "có xoá bằng chứng không" để test được bằng endpoint giả (lab).
    public static async Task<bool> SubmitAsync(CrashReportInfo info, string endpointUrl, string pendingPath)
    {
        string body = BuildBody(info);

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(6);
            client.DefaultRequestHeaders.Add("User-Agent", "WarpGameAccelerator-Telemetry");

            var payload = new { content = body };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(endpointUrl, content);

            if (!response.IsSuccessStatusCode) return false;    // GIỮ lại vết để hỏi lần sau

            ClearPendingCrashReport(pendingPath);
            return true;
        }
        catch
        {
            return false;                                        // mất mạng ≠ mất bằng chứng
        }
    }

    public static string BuildBody(CrashReportInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Crash tự động** — v{info.AppVersion} · {info.Timestamp}");
        sb.AppendLine($"**OS:** {info.OsVersion}");
        sb.AppendLine($"**Loại:** `{info.ExceptionType}`");
        sb.AppendLine($"```text\n{info.Message}\n```");
        sb.AppendLine($"**Stack:**");
        sb.AppendLine($"```text\n{info.StackTrace}\n```");
        return BugReportService.ClampKeepingFences(sb.ToString(), BugReportService.MaxMessageChars);
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
