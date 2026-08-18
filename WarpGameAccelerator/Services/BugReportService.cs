// ============================================================
// Services/BugReportService.cs
// Gửi báo lỗi do user chủ động bấm gửi — đóng gói log liên quan
// theo category rồi post lên Discord webhook (KHÔNG dùng GitHub
// API vì cần token, nhúng token write-access vào exe công khai
// là rủi ro rò rỉ; webhook Discord chỉ có quyền post 1 channel,
// rủi ro thấp hơn nhiều nếu bị trích xuất từ exe).
// ============================================================
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarpGameAccelerator.Services;

public enum BugReportCategory
{
    Disconnect,
    Lag,
    MultiClient,
    WarpAccount,
    Update,
    Other
}

public class BugReportService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "bug_report_config.json");

    private static readonly string TraceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Logs", "trace.log");

    private static readonly string MihomoLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Core", "mihomo_runtime.log");

    // Giới hạn để không vượt payload Discord (embed field tối đa ~1024 ký tự,
    // message tổng ~2000-6000 tùy loại) — chỉ lấy phần liên quan, không gửi nguyên file.
    private const int MaxLogCharsPerAttachment = 1500;

    private record CategoryRule(string DisplayNameVi, string DisplayNameEn, string[] Keywords, bool IncludeMihomoLog, bool IncludeTraceLog);

    private static readonly Dictionary<BugReportCategory, CategoryRule> Rules = new()
    {
        [BugReportCategory.Disconnect] = new("Mất kết nối / rớt mạng khi chơi", "Disconnect / dropped while playing",
            new[] { "deadline exceeded", "dial", "WireGuard", "handshake" }, true, true),
        [BugReportCategory.Lag] = new("Giật, lag không rớt hẳn", "Lag / stutter without full disconnect",
            new[] { "deadline exceeded", "ICMP", "timeout" }, true, false),
        [BugReportCategory.MultiClient] = new("Multi-Client không mở được", "Multi-Client fails to launch",
            new[] { "StartBtn_Click", "LaunchClientsToTotal", "client", "helper" }, false, true),
        [BugReportCategory.WarpAccount] = new("Không kết nối WARP / lấy token lỗi", "WARP connect / token failure",
            new[] { "WarpAccount", "token", "TIMEOUT" }, false, true),
        [BugReportCategory.Update] = new("Cập nhật app lỗi", "App update failure",
            new[] { "update", "Cap nhat", "cập nhật" }, false, true),
        [BugReportCategory.Other] = new("Khác", "Other",
            Array.Empty<string>(), true, true),
    };

    public static string GetAttachmentPreview(BugReportCategory category, bool vietnamese)
    {
        var rule = Rules[category];
        var parts = new List<string>();
        if (rule.IncludeMihomoLog) parts.Add("mihomo_runtime.log");
        if (rule.IncludeTraceLog) parts.Add("trace.log");
        var files = parts.Count > 0 ? string.Join(" + ", parts) : (vietnamese ? "không có log phù hợp" : "no matching log");
        return vietnamese ? $"Sẽ đính kèm: {files}" : $"Will attach: {files}";
    }

    private static bool IsConfigured()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var json = JsonNode.Parse(File.ReadAllText(ConfigPath));
            var url = json?["WebhookUrl"]?.ToString();
            return !string.IsNullOrWhiteSpace(url) && url.StartsWith("https://discord.com/api/webhooks/");
        }
        catch { return false; }
    }

    private static string? GetWebhookUrl()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = JsonNode.Parse(File.ReadAllText(ConfigPath));
            var url = json?["WebhookUrl"]?.ToString();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch { return null; }
    }

    // ── Lọc log: ưu tiên dòng khớp keyword của category, fallback lấy tail nếu không đủ dòng ──
    private static string ExtractRelevantLines(string filePath, string[] keywords, int maxChars)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var allLines = File.ReadAllLines(filePath);
            IEnumerable<string> tailLines = allLines.Length > 300 ? allLines[^300..] : allLines;

            List<string> chosen;
            if (keywords.Length > 0)
            {
                chosen = tailLines.Where(l => keywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
                if (chosen.Count == 0) chosen = tailLines.TakeLast(40).ToList();
            }
            else
            {
                chosen = tailLines.TakeLast(60).ToList();
            }

            // Cắt theo TỪNG DÒNG nguyên vẹn (bỏ dòng cũ nhất trước), không cắt giữa
            // dòng theo số ký tự — cắt theo char có thể chặt đứt 1 dòng thành 2 mảnh
            // vô nghĩa (vd "[11:33:43.778]" còn lại "3:43.778]").
            int totalChars = chosen.Sum(l => l.Length + 1);
            int dropped = 0;
            while (totalChars > maxChars && chosen.Count > 1)
            {
                totalChars -= chosen[0].Length + 1;
                chosen.RemoveAt(0);
                dropped++;
            }

            var text = string.Join('\n', chosen);
            if (dropped > 0) text = $"...(đã cắt {dropped} dòng cũ hơn)...\n" + text;
            return text;
        }
        catch { return string.Empty; }
    }

    public static async Task<(bool Success, string Message)> SendAsync(BugReportCategory category, string userDescription)
    {
        if (string.IsNullOrWhiteSpace(userDescription))
            return (false, "Chưa nhập mô tả.");

        var webhookUrl = GetWebhookUrl();
        if (webhookUrl == null)
            return (false, "Chưa cấu hình kênh gửi báo lỗi (bug_report_config.json).");

        var rule = Rules[category];

        var sb = new StringBuilder();
        sb.AppendLine($"**Loại lỗi:** {rule.DisplayNameVi}");
        sb.AppendLine($"**Mô tả:** {userDescription}");
        sb.AppendLine($"**Version:** v{GetAppVersion()}   **Thời gian:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (rule.IncludeMihomoLog)
        {
            var mihomoExcerpt = ExtractRelevantLines(MihomoLogPath, rule.Keywords, MaxLogCharsPerAttachment);
            if (!string.IsNullOrWhiteSpace(mihomoExcerpt))
                sb.AppendLine($"\n**mihomo_runtime.log:**\n```\n{mihomoExcerpt}\n```");
        }
        if (rule.IncludeTraceLog)
        {
            var traceExcerpt = ExtractRelevantLines(TraceLogPath, rule.Keywords, MaxLogCharsPerAttachment);
            if (!string.IsNullOrWhiteSpace(traceExcerpt))
                sb.AppendLine($"\n**trace.log:**\n```\n{traceExcerpt}\n```");
        }

        var content = sb.ToString();
        // Discord message content giới hạn 2000 ký tự — cắt an toàn nếu vượt.
        if (content.Length > 1900) content = content[..1900] + "\n...(cắt bớt)...";

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var payload = new { content };
            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(webhookUrl, body);

            return response.IsSuccessStatusCode
                ? (true, "Đã gửi báo lỗi, cảm ơn bạn!")
                : (false, $"Gửi thất bại (mã {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi khi gửi: {ex.Message}");
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }
        catch { return "0.0.0"; }
    }
}
