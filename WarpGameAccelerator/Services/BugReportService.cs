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
using System.Text.RegularExpressions;

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

    private static readonly string CoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Core");

    // Người dùng hay làm đúng điều app khuyên: Stop/Start Boost rồi mới bấm "Gửi báo lỗi".
    // Khi đó mihomo_runtime.log của phiên gây sự cố đã được SessionLogArchive đổi tên vào kho,
    // và file hiện tại thì gần như trống. Chọn file CÓ NỘI DUNG mới nhất, thay vì mù quáng lấy current.
    internal static string ResolveMihomoLogPath(string coreDir)
    {
        var current = Path.Combine(coreDir, "mihomo_runtime.log");
        try
        {
            if (File.Exists(current) && new FileInfo(current).Length > 0) return current;

            var archive = Path.Combine(coreDir, "sessions");
            if (Directory.Exists(archive))
            {
                var newest = new DirectoryInfo(archive).GetFiles("mihomo_runtime-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (newest is not null && newest.Length > 0) return newest.FullName;
            }
        }
        catch { }
        return current;
    }

    // Giới hạn để không vượt payload Discord (embed field tối đa ~1024 ký tự,
    // message tổng ~2000-6000 tùy loại) — chỉ lấy phần liên quan, không gửi nguyên file.
    private const int MaxLogCharsPerAttachment = 1500;

    // Discord giới hạn message content 2000 ký tự; chừa 100 làm đệm an toàn.
    internal const int MaxMessageChars = 1900;

    /// <summary>Trần của khối bằng chứng máy (#39) — ≈21% payload, đổi lại phần log bị trừ ngân sách.</summary>
    internal const int EvidenceBudgetChars = 400;

    private record CategoryRule(string DisplayNameVi, string DisplayNameEn, string[] Keywords, bool IncludeMihomoLog, bool IncludeTraceLog);

    private static readonly Dictionary<BugReportCategory, CategoryRule> Rules = new()
    {
        [BugReportCategory.Disconnect] = new("Mất kết nối / rớt mạng khi chơi", "Disconnect / dropped while playing",
            new[] { "deadline exceeded", "dial", "WireGuard", "handshake" }, true, true),
        [BugReportCategory.Lag] = new("Giật, lag không rớt hẳn", "Lag / stutter without full disconnect",
            // "ICMP" từng là từ khoá ở đây — tức bộ lọc CHỦ ĐỘNG gom đúng loại dòng mà dự án đã kết
            // luận là nhiễu (đo: evidence "Lag" có tới 38–100% là ICMP giả lập). Thay bằng điều kiện
            // dương "] dial" theo đúng quy tắc đã ghi trong CLAUDE.md.
            new[] { "] dial", "deadline exceeded", "timeout" }, true, false),
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

    // Kênh gửi dùng chung cho cả báo lỗi thủ công và báo cáo crash tự động.
    internal static string? GetWebhookUrl()
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
            var window = (allLines.Length > 300 ? allLines[^300..] : allLines).ToList();

            // mihomo mô phỏng ICMP cho TUN, nên "receive ICMP echo reply: … i/o timeout" là dòng
            // NHIỄU CÓ CHỦ ĐÍCH: nó level=error và trỏ đúng IP server game, tức dễ bị đọc thành
            // "mất kết nối" nhất. CLAUDE.md đã ghi điều này sau 3 báo cáo sai; đo trên code thật
            // (experiments/bug-report-filter/) thì evidence category "Lag" từng là 38–100% loại này.
            // Gỡ ở đây thay vì chỉ sửa từ khoá, để MỌI category không còn mang nhiễu.
            int icmpNoise = window.Count(l => l.Contains("receive ICMP echo reply", StringComparison.OrdinalIgnoreCase));
            if (icmpNoise > 0)
                window = window.Where(l => !l.Contains("receive ICMP echo reply", StringComparison.OrdinalIgnoreCase)).ToList();

            List<string> chosen;
            if (keywords.Length > 0)
            {
                chosen = window.Where(l => keywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
                if (chosen.Count == 0) chosen = window.TakeLast(40).ToList();
            }
            else
            {
                chosen = window.TakeLast(60).ToList();
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
            // Ghi rõ đã bỏ gì, để "0 dòng lỗi" không bị đọc thành "không có vấn đề".
            if (icmpNoise > 0) text = $"...(đã bỏ {icmpNoise} dòng ICMP giả lập của mihomo — không phải mất gói thật)...\n" + text;
            text = ProvenanceNote(chosen) + text;
            return text;
        }
        catch { return string.Empty; }
    }

    private static readonly Regex SessionTagRx = new(@"\[P(\d+) S([0-9a-fA-F]{6}) T\d+\]", RegexOptions.Compiled);

    /// <summary>#25 — nói rõ bằng chứng gửi đi thuộc PHIÊN NÀO, thay vì im lặng.</summary>
    /// Lý do có số liệu (batch 37): các phòng thí nghiệm biên dịch CHÍNH DiagnosticLogService đã ghi
    /// lẫn vào trace.log của người dùng (73/73 dòng định dạng mới thuộc PID lạ), và reporter lấy cửa
    /// sổ "phần mới nhất" ⇒ 98% evidence của Disconnect/Update/Khác là dòng do lab tạo. Không cách nào
    /// chặn mọi nguồn nhiễm, nhưng nói ra thì người đọc báo cáo tự biết phải nghi cái gì. Trả về chuỗi
    /// trống khi cửa sổ chỉ một phiên — báo cáo sạch thì đừng thêm nhiễu.
    internal static string ProvenanceNote(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return string.Empty;

        var perSession = new Dictionary<string, int>();
        int legacy = 0;
        foreach (var l in lines)
        {
            var m = SessionTagRx.Match(l);
            if (!m.Success) { legacy++; continue; }
            var key = $"P{m.Groups[1].Value}·S{m.Groups[2].Value}";
            perSession[key] = perSession.GetValueOrDefault(key) + 1;
        }

        if (perSession.Count == 0)
            return $"...(bằng chứng: {lines.Count} dòng đều là định dạng CŨ không có PID/session ⇒ không xác định được thuộc phiên nào — bản app trước khi có #1)...\n";
        if (perSession.Count == 1 && legacy == 0) return string.Empty;

        var ordered = perSession.OrderByDescending(k => k.Value).ToList();
        // Gioi han 6 phien trong danh sach: do tren log that cua may nay, cua so 60 dong cuoi chua
        // toi 19 phien ⇒ liet ke het = ~900 ky tu cho rieng dong chu thich, an mat cho cua bang
        // chung. Nguoi doc can biet "nhieu phien lan vao nhau", khong can bản kê đầy đủ.
        const int listed = 6;
        var parts = ordered.Take(listed).Select(k => $"{k.Key}×{k.Value}");
        var more = ordered.Count > listed ? $"; +{ordered.Count - listed} phiên khác" : "";
        var tail = legacy > 0 ? $"; {legacy} dòng định dạng cũ không PID" : "";
        return $"...(bằng chứng gồm {perSession.Count} phiên: {string.Join(", ", parts)}{more}{tail} — phiên nào KHÔNG phải của bản đang chạy là dữ liệu từ tiến trình khác)...\n";
    }

    public static async Task<(bool Success, string Message)> SendAsync(BugReportCategory category, string userDescription)
    {
        if (string.IsNullOrWhiteSpace(userDescription))
            return (false, "Chưa nhập mô tả.");

        var webhookUrl = GetWebhookUrl();
        if (webhookUrl == null)
            return (false, "Chưa cấu hình kênh gửi báo lỗi (bug_report_config.json).");

        // #39: dựng payload giờ ĐỌC THÊM Event Log + thư mục hồ sơ treo (IO), không được nằm
        // trước await đầu tiên của một lệnh chạy trên UI thread (FR-16, vách 5,1 s của watchdog).
        var content = await System.Threading.Tasks.Task.Run(
            () => BuildReportBody(category, userDescription, ResolveMihomoLogPath(CoreDir), TraceLogPath));

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

    // Ghép payload độc lập với mạng để test được (experiments/bug-report-filter/).
    public static string BuildReportBody(BugReportCategory category, string userDescription,
                                         string mihomoLogPath, string traceLogPath, string? capturesDir = null)
    {
        var rule = Rules[category];

        var header = new StringBuilder();
        header.AppendLine($"**Loại lỗi:** {rule.DisplayNameVi}");
        header.AppendLine($"**Mô tả:** {userDescription}");
        header.AppendLine($"**Version:** v{GetAppVersion()}   **Thời gian:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // Mỗi attachment từng bị giới hạn 1500 ký tự CỐ ĐỊNH, trong khi message tổng chỉ còn ~1900
        // sau phần đầu; và khi vượt thì code cũ cắt `content[..1900]` — cắt từ ĐẦU, tức giữ lại nhan đề
        // và vứt đúng phần bằng chứng phía CUỐI (đo: trace.log chỉ còn 237/1500 ký tự = 16%).
        // Nay chia phần ngân sách còn lại cho các khối log, và để ExtractRelevantLines bỏ dòng CŨ nhất
        // cho tới khi vừa — luôn giữ được đoạn sát thời điểm xảy ra sự cố.
        bool twoBlocks = rule.IncludeMihomoLog && rule.IncludeTraceLog;
        // Bằng chứng máy (Event Log + hồ sơ treo phiên trước) được tính vào overhead TRƯỚC khi chia
        // ngân sách cho log: nếu để nó đứng cuối, ClampKeepingFences sẽ cắt đúng khối vừa dựng —
        // và đó chính xác là cái lỗi đã khiến 84% trace bị vứt ở batch 16.
        var evidence = BuildEvidenceBlock(capturesDir);
        int overhead = header.Length + (twoBlocks ? 240 : 140) + (evidence.Length > 0 ? evidence.Length + 12 : 0);
        int budget = Math.Max(0, MaxMessageChars - overhead);
        int perAttach = Math.Min(MaxLogCharsPerAttachment, twoBlocks ? budget / 2 : budget);

        var sb = new StringBuilder(header.ToString());

        if (rule.IncludeMihomoLog && perAttach > 0)
        {
            var excerpt = ExtractRelevantLines(mihomoLogPath, rule.Keywords, perAttach);
            if (!string.IsNullOrWhiteSpace(excerpt))
                // Ghi đúng TÊN file đang đính kèm: độc giả cần biết đây là phiên hiện tại hay một
                // phiên đã lưu trong kho (SessionLogArchive).
                sb.AppendLine($"\n**{Path.GetFileName(mihomoLogPath)}:**\n```\n{excerpt}\n```");
        }
        if (rule.IncludeTraceLog && perAttach > 0)
        {
            var excerpt = ExtractRelevantLines(traceLogPath, rule.Keywords, perAttach);
            if (!string.IsNullOrWhiteSpace(excerpt))
                sb.AppendLine($"\n**trace.log:**\n```\n{excerpt}\n```");
        }

        if (evidence.Length > 0) sb.AppendLine("\n" + evidence);

        return ClampKeepingFences(sb.ToString(), MaxMessageChars);
    }

    /// <summary>
    /// #39 — hai kho bằng chứng đã TỒN TẠI trên đĩa mà báo lỗi chưa từng đọc: đo ngày 01-11,
    /// `EventLogEvidence` có 0 điểm gọi trong production (class + lab E1–E19 chạy ở cổng riêng),
    /// còn `HangCaptures` chỉ đối thoại khởi động chạm tới. Không có khối này, một cú treo trên
    /// máy người dùng gửi báo cáo về đúng lúc app đứng lại sẽ mang… 0 bằng chứng về cái treo đó.
    /// Trả chuỗi rỗng khi cả hai kênh im lặng — khối bằng chứng không bao giờ được làm hỏng báo lỗi.
    /// </summary>
    internal static string BuildEvidenceBlock(string? capturesDir = null)
    {
        var sb = new StringBuilder();
        try
        {
            var read = EventLogEvidence.ReadLocalChecked(30);
            var summary = EventLogEvidence.Summarize(read.Items,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 200);
            if (!string.IsNullOrWhiteSpace(summary))
                sb.AppendLine($"**Event Log 30 ngày:** {summary}");
            else if (read.Error is not null)
                sb.AppendLine($"**Event Log:** kênh hỏng — {read.Error}");
        }
        catch { }
        try
        {
            var inc = HangWatchdog.FindPreviousIncident(capturesDir ?? HangWatchdog.CapturesDir,
                                                        HangWatchdog.SessionTag);
            if (inc is not null)
            {
                string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;
                sb.AppendLine($"**Hồ sơ treo lần trước** {inc.Value.Time} (phiên {inc.Value.Session}): " +
                              $"{Cut(inc.Value.Verdict, 90)} · dấu vết: {Cut(inc.Value.TopBreadcrumb, 90)}");
            }
        }
        catch { }
        var text = sb.ToString().TrimEnd();
        return text.Length > EvidenceBudgetChars ? ClampKeepingFences(text, EvidenceBudgetChars) : text;
    }

    // Lưới an toàn cuối: nếu vẫn vượt (nhan đề/description bất thường), cắt ở CUỐI khối và đóng
    // fence lại — một code fence hở làm cả thông báo Discord hiển thị sai, hỏng luôn bằng chứng.
    internal static string ClampKeepingFences(string text, int limit)
    {
        if (text.Length <= limit) return text;

        var cut = text[..(limit - 8)];
        if ((cut.Count(i => i == '`') % 2) != 0)   // số backtick lẻ = đang đứng giữa một fence
            cut += "\n```";
        return cut + "\n...(cắt)...";
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
