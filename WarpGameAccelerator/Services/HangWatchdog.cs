// ============================================================
// Services/HangWatchdog.cs — ứng viên #2 + #21 (spec specs/001-hang-watchdog)
//
// Vì sao tồn tại (số liệu): 48 lần Windows ghi nhận app treo, 0 dump, 0 hồ sơ — vì mọi thứ để
// bắt lỗi (CrashReportService) chỉ hook UnhandledException, còn treo thì không ném gì.
//
// Vì sao phải là TÍN HIỆU NỘI BỘ (batch 22 đo): với đúng hình dạng kẹt trong ShowWindow
// cross-process, mọi dấu hiệu của Windows đều báo khoẻ — SendMessageTimeout(WM_NULL) 0/39 lượt,
// IsHungAppWindow đúng 1/~80 lượt, Process.Responding = True. Nên không có bộ canh ngoài nào
// kích hoạt được.
//
// Cách phát hiện: gửi một work item "thăm dò" xuống chính hàng đợi UI và đo thời gian nó được chạy.
// UI khoẻ ⇒ vòng phản hồi cỡ ms. UI kẹt trong một lời gọi blocking ⇒ không bao giờ phản hồi ⇒
// sau StallThresholdMs và ConfirmationsRequired lượt liên tiếp thì xác nhận treo.
//
// Hồ sơ nghiệp vụ (FR-02/#21): app KHÔNG đọc được stack của thread đang kẹt từ trong tiến trình,
// nên breadcrumb là nguồn "dừng ở đâu": mỗi thao tác dài tự đánh dấu (Mark) kèm tên hàm + tên tệp +
// số dòng do compiler điền. Hồ sơ ghi THÀNH VĂN BẢN vào data dir của app TRƯỚC khi FailFast — thư
// mục dump của OS không đáng tin (batch 21: tự cắt 2 lần trong 15 phút). Message FailFast bị cắt ở
// MaxReportChars vì qua trần là mất CẢ sự kiện .NET Runtime 1025 (batch 21: 31.286 OK / 32.000 mất).
// ============================================================
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WarpGameAccelerator.Services;

/// <summary>Lớp bơm sự sống cho UI. Tách ra để HangWatchdog kiểm được bằng pump giả
/// (experiments/hang-watchdog) mà không phải kéo WinUI vào phòng thí nghiệm.</summary>
public interface IUiLivenessPump
{
    /// <returns>false nếu không giao được công việc (UI đã chết hẳn / hàng đợi đóng).</returns>
    bool TryPost(Action work);
}

public sealed class HangWatchdog : IDisposable
{
    public sealed class Options
    {
        public int ProbeIntervalMs = 2000;
        public int StallThresholdMs = 4000;
        public int ConfirmationsRequired = 2;
        public int MaxSleepOvershootMs = 1500;  // sleep quá ProbeIntervalMs + ngưỡng này = cả process bị OS tạm dừng
        public int MaxReportChars = 24000;      // trần AN TOÀN (cliff thật đo được ~31,3 KB)
        public int BreadcrumbCapacity = 48;
    }

    private readonly IUiLivenessPump _pump;
    private readonly Options _opt;
    private readonly Action<string> _terminate;
    private readonly Func<string, string> _writeRecord;   // nội dung → đường dẫn hồ sơ
    private readonly Action<int> _sleep;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<(string Text, int Tid)> _crumbs = new();

    private long _postedTicks;
    private long _ackedTicks;
    private long _startedTicks;
    private long _pauseResetTicks;
    private DateTime _startedAt;
    private int _unconfirmed;
    private int _uiThreadId;                      // thread nào trả lời vòng thăm dò = thread UI
    private volatile bool _stopping;              // người dùng chủ động thoát (FR-11)
    private volatile bool _fired;
    private Thread? _loop;

    public static readonly string SessionTag = Guid.NewGuid().ToString("N")[..6];

    /// <summary>Nơi hồ sơ treo nằm trên đĩa — MỘT nguồn sự thật cho cả writer, dialog khởi động
    /// và tầng báo lỗi (#39). Ba chỗ tự nối chuỗi đường dẫn là ba chỗ lệch nhau được.</summary>
    public static string CapturesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "HangCaptures");

    public HangWatchdog(IUiLivenessPump pump, Options? options = null,
                        Action<string>? terminate = null, Func<string, string>? writeRecord = null,
                        Action<int>? sleep = null)
    {
        _pump = pump;
        _opt = options ?? new Options();
        // Mặc định của production: ghi hồ sơ rồi FailFast (runtime/Windows tự viết dump kèm theo).
        _terminate = terminate ?? (message => Environment.FailFast(message));
        _writeRecord = writeRecord ?? (content =>
        {
            var dir = CapturesDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"hang-P{Environment.ProcessId}-S{SessionTag}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, content);
            return path;
        });
        _sleep = sleep ?? Thread.Sleep;
    }

    public void Start()
    {
        _startedTicks = Stopwatch.GetTimestamp();
        _startedAt = DateTime.Now;
        _loop = new Thread(Loop) { IsBackground = true, Name = "HangWatchdog" };
        _loop.Start();
    }

    /// <summary>FR-11: đang thoát theo ý người dùng thì không được báo treo.</summary>
    public void BeginShutdown() => _stopping = true;

    public static void Mark(string operation,
                            [CallerMemberName] string member = "",
                            [CallerFilePath] string file = "",
                            [CallerLineNumber] int line = 0)
    {
        // Chỉ là một dòng enqueue vào vòng chữ nhỏ; không chạm UI thread.
        Current?.MarkInternal(operation, member, file, line);
    }

    public static HangWatchdog? Current { get; set; }

    private void MarkInternal(string operation, string member, string file, int line)
    {
        // tid được ghi LÚC MARK (thread nào đang chạy thao tác), còn nhãn UI/nền dán LÚC ĐỌC hồ sơ:
        // lúc này _uiThreadId đã chắc chắn có (ack đầu tiên phải xảy ra trước khi UI kịp kẹt).
        var crumb = $"[{DateTime.Now:HH:mm:ss.fff}] {operation} @ {Path.GetFileName(file)}:{line} ({member})";
        lock (_gate)
        {
            _crumbs.Enqueue((crumb, Environment.CurrentManagedThreadId));
            while (_crumbs.Count > _opt.BreadcrumbCapacity) _crumbs.Dequeue();
        }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_stopping) return;

                long postAt = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _postedTicks, postAt);

                bool accepted = _pump.TryPost(() =>
                {
                    Interlocked.Exchange(ref _ackedTicks, Stopwatch.GetTimestamp());
                    // Ai TRẢ LỜI vòng thăm dò chính là thread mà app phải giữ cho sống. Ghi lại một
                    // lần: không suy ra từ tên thread (WinUI không đặt tên), mà từ việc nó chạy work.
                    if (Volatile.Read(ref _uiThreadId) == 0)
                        Interlocked.Exchange(ref _uiThreadId, Environment.CurrentManagedThreadId);
                });
                if (!accepted)
                {
                    // Hàng đợi từ chối: UI không còn lấy lệnh nữa — cũng là chết. Vẫn phải đếm đủ
                    // ConfirmationsRequired (bản đầu `return` ngay nên không bao giờ tới ngưỡng 2 —
                    // phòng thí nghiệm T4 bắt được).
                    _sleep(_opt.ProbeIntervalMs);
                    if (_stopping || _cts.IsCancellationRequested) return;
                    if (ShouldFire(Stopwatch.GetTimestamp()))
                        Fire("hàng đợi UI từ chối công việc (TryEnqueue trả false)");
                    continue;
                }

                _sleep(_opt.ProbeIntervalMs);
                if (_stopping || _cts.IsCancellationRequested) return;

                long now = Stopwatch.GetTimestamp();
                long acked = Interlocked.Read(ref _ackedTicks);

                // Nếu chính probe vừa gửi ở lượt này đã được UI trả lời (acked >= postAt) ⇒ UI đang
                // sống và có tiến triển. Mọi khoảng (now - acked) lớn lúc này chỉ đến từ việc chính
                // Thread.Sleep của watchdog bị OS đánh thức muộn (đã chứng minh trên dump thật: UI ack
                // trong 0,3 ms nhưng sleep kéo dài 6,4 s / 24,2 s ⇒ báo động giả).
                if (acked >= postAt)
                {
                    lock (_gate) _unconfirmed = 0;
                    continue;
                }

                // Nếu chính thread nền watchdog ngủ lố quá ProbeIntervalMs + MaxSleepOvershootMs ⇒
                // cả tiến trình / hệ thống đang bị OS tạm dừng (sleep/wake, boot storm, CPU/IO nghẽn
                // toàn cục), không có bằng chứng riêng UI bị treo. Đẩy mốc đo sang `now` để sau khi
                // hết nghẽn vẫn bắt được treo thật nếu UI tiếp tục kẹt.
                double sleepActualMs = (now - postAt) * 1000.0 / Stopwatch.Frequency;
                if (sleepActualMs > _opt.ProbeIntervalMs + _opt.MaxSleepOvershootMs)
                {
                    Interlocked.Exchange(ref _pauseResetTicks, now);
                    lock (_gate) _unconfirmed = 0;
                    continue;
                }

                // Chưa có ack nào KHÔNG phải bằng chứng treo — đó là trạng thái "chưa biết".
                // Lấy mốc khởi động (hoặc mốc sau OS pause) thay thế; nếu UI thực sự không ack lần
                // nào thì tuổi sẽ vượt ngưỡng và vẫn fired. (Bản đầu tính MaxValue ngay từ lượt đầu ⇒
                // đếm 2 lượt liên tiếp chỉ trong ~600ms và fired khi app còn khoẻ — lab T2 bắt được.)
                long baseline = acked != 0 ? acked : _startedTicks;
                long pauseReset = Interlocked.Read(ref _pauseResetTicks);
                long reference = pauseReset > baseline ? pauseReset : baseline;
                double ackAgeMs = (now - reference) * 1000.0 / Stopwatch.Frequency;

                // Tiêu chí là "KHÔNG CÓ ack nào trong Ngưỡng" — tức mất tiến triển hẳn.
                // Bản đầu dùng thêm "acked < postedTicks" và đã báo NHẦM trong phòng thí nghiệm
                // (T2: UI vẫn chạy xong mỗi việc, chỉ chậm hơn nhịp thăm dò ⇒ hàng đợi thăm dò
                // chất đống ⇒ điều kiện đó luôn đúng ⇒ firing). Một UI chậm-but-còn-tiến-triển là
                // chuyện HIỆU NĂNG, không phải treo; FR-06 cấm giết nó.
                if (ackAgeMs >= _opt.StallThresholdMs)
                {
                    if (ShouldFire(now))
                        Fire($"UI không phản hồi vòng thăm dò trong {ackAgeMs:F0}ms (ngưỡng {_opt.StallThresholdMs}ms, " +
                             $"{_unconfirmed} lượt liên tiếp)");
                }
                else
                {
                    lock (_gate) _unconfirmed = 0;
                }
            }
            catch { /* watchdog không được phép làm app chết vì lỗi của chính nó */ }
        }
    }

    private bool ShouldFire(long now)
    {
        lock (_gate)
        {
            _unconfirmed++;
            return _unconfirmed >= _opt.ConfirmationsRequired && !_fired;
        }
    }

    private void Fire(string verdict)
    {
        lock (_gate)
        {
            if (_fired) return;
            _fired = true;
        }

        string record = BuildRecord(verdict);
        string path;
        try { path = _writeRecord(record); }
        catch (Exception ex) { path = $"(không ghi được hồ sơ: {ex.GetType().Name})"; }

        try { DiagnosticLogService.Trace($"[HangWatchdog] Treo được xác nhận: {verdict} — hồ sơ: {path}"); }
        catch { }

        // Message đi vào Event Log (.NET Runtime 1025) ⇒ phải cắt CHỦ ĐỘNG dưới trần.
        _terminate(CapForEventLog(record));
    }

    /// <summary>
    /// Đếm "app đã được MỞ lại bao nhiêu lần hôm nay" — biến mà batch 43 chỉ ra là giả thuyết còn sống
    /// duy nhất: 46% số treo rơi vào 4 ngày mà chủ máy build đi build lại (có bản cách nhau 6,6 phút),
    /// và trên các ngày đó 82% treo nằm ở 06–09h so với 35% ngày thường ⇒ "mấy giờ" có thể chỉ là cái
    /// bóng của "phiên thứ mấy". Không có con số này trong hồ sơ thì lần treo kế tiếp vẫn không phân
    /// định được hai giả thuyết. Hàm thuần (đường dẫn + mốc thời gian truyền vào) để kiểm chứng được.
    /// Đếm bằng chuỗi ASCII "APP KH" vì chữ Đ/Ộ/Ị không nằm trong pattern an toàn (bài học batch 39).
    /// </summary>
    internal static (int today, int undated) CountSessionsToday(string logPath, DateTime now, int maxLines = 500_000)
    {
        int today = 0, undated = 0, seen = 0;
        string day = now.ToString("yyyy-MM-dd");
        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (++seen > maxLines) break;                    // chặn work cho lúc app đang kẹt
                if (!line.Contains("APP KH")) continue;
                if (line.StartsWith($"[{day}")) { today++; continue; }
                // Dinh dang dated la "[yyyy-MM-dd " — dau cach o chi so 11 (ban dau danh chi so 10 ⇒
                // marker cua ngay khac bi dem thanh 'khong ngay'; T9b cua lab bat duoc).
                bool hasDate = line.Length >= 12 && line[0] == '[' && char.IsDigit(line[1])
                               && line[5] == '-' && line[8] == '-' && line[11] == ' ';
                if (hasDate) continue;                    // marker của ngày khác, có ngày rõ ràng
                undated++;
            }
        }
        catch { /* hồ sơ không được phép chết vì một phép đếm */ }
        return (today, undated);
    }

    internal string BuildRecord(string verdict)
    {
        (string Text, int Tid)[] marks;
        int uiTid;
        lock (_gate) { marks = _crumbs.ToArray(); uiTid = Volatile.Read(ref _uiThreadId); }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== HANG REPORT (FR-02/FR-03) ===");
        sb.AppendLine($"pid={Environment.ProcessId} session={SessionTag} time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"verdict={verdict}");
        sb.AppendLine($"threshold={_opt.StallThresholdMs}ms probeInterval={_opt.ProbeIntervalMs}ms confirmations={_opt.ConfirmationsRequired}");
        sb.AppendLine($"version={typeof(HangWatchdog).Assembly.GetName().Version}");

        // Bối cảnh phiên (batch 38 do, không phải phụ trang trang trí): 48 lần treo nằm trên chỉ
        // 12/70 ngày, và 56% rơi vào khung 06–09h trong khi khung đó chỉ chiếm 16% hoạt động ghi
        // trong trace.log ⇒ gấp 3,55 lần theo cường độ. "Phiên sáng sớm" là giả thuyết duy nhất còn
        // sống sau khi boot/wake/Windows-update đã bị loại, nên hồ sơ phải lưu sẵn ba con số để lần
        // treo kế tiếp TỰ kiểm chứng được, thay vì chờ một phép đo thủ công khác.
        double appUpMin = _startedAt == default ? -1 : (DateTime.Now - _startedAt).TotalMinutes;
        double machUpH = Environment.TickCount64 / 3_600_000.0;
        int nowHour = DateTime.Now.Hour;
        string block = nowHour < 6 ? "đêm (00–05h)" : nowHour < 10 ? "SÁNG SỚM (06–09h) — khung 3,55×"
                     : nowHour < 14 ? "trưa (10–13h)" : "chiều–tối (14–23h)";
        sb.AppendLine("bối cảnh phiên:");
        sb.AppendLine($"  ứng dụng đã chạy: {(appUpMin < 0 ? "(chưa Start)" : $"{appUpMin:F1} phút, từ {_startedAt:HH:mm:ss}")}");
        sb.AppendLine($"  máy đã bật: {machUpH:F2} giờ");
        sb.AppendLine($"  khung giờ: {nowHour:00}h — {block}");
        var (sessionsToday, undated) = CountSessionsToday(DiagnosticLogService.LogPath, DateTime.Now);
        sb.AppendLine($"  số lần MỞ app hôm nay đếm được trong log: {sessionsToday}" +
                      (sessionsToday == 0 ? " (log hiện hành không có marker khởi động nào — bản thật ghi một dòng lúc mở app)"
                                          : " (đã gồm phiên này, vì marker của nó ghi từ lúc mở)") +
                      (undated > 0 ? $"; {undated} marker ở log định dạng cũ không ngày ⇒ không xếp được" : "") +
                      " — biến quyết định giả thuyết 'tuổi phiên' của batch 43");

        // Tiêu đề cũ viết "thao tác đang chạy khi kẹt" mà crumb KHÔNG nói chạy trên thread nào — trong
        // khi cả hai chỗ Mark() của production đều ở thread nền. Hồ sơ vì thế mô tả một thread đang
        // khoẻ, còn thread đang kẹt thì câm. Nay mỗi crumb mang tid, và dòng tóm tắt tự nhận blind spot.
        int uiMarks = uiTid == 0 ? 0 : marks.Count(m => m.Tid == uiTid);
        sb.AppendLine($"chuyến đánh dấu gần nhất (thread UI = {(uiTid == 0 ? "chưa xác định được (chưa có lượt ack nào)" : uiTid.ToString())}; crumb thuộc UI: {uiMarks}/{marks.Length}):");
        if (marks.Length == 0) sb.AppendLine("  (không có — UI kẹt trước thao tác được đánh dấu đầu tiên)");
        foreach (var m in marks)
            sb.AppendLine($"  {m.Text} tid={m.Tid} → {(uiTid == 0 ? "không quy được" : m.Tid == uiTid ? "thread UI" : "nền")}");
        if (marks.Length > 0 && uiMarks == 0)
            sb.AppendLine("  ⚠ KHÔNG có dấu vết nào do thread UI ghi ⇒ hồ sơ chưa chỉ ra được thao tác nào của UI đang chạy;" +
                          " muốn biết phải thêm Mark() vào đúng các hàm chạy trên UI thread (xem #24).");
        return sb.ToString();
    }

    // Phải chừa chỗ cho CHÍNH hậu tố: bản đầu cắt đúng trần rồi NỐI hậu tố ⇒ kết quả vượt trần
    // (đo: 24.041 > 24.000) — tức hỏng đúng điều nó sinh ra để tránh.
    internal string CapForEventLog(string record)
    {
        if (record.Length <= _opt.MaxReportChars) return record;
        var tail = $"\n…(cắt chủ động: {record.Length} ký tự, trần {_opt.MaxReportChars})";
        int keep = Math.Max(0, _opt.MaxReportChars - tail.Length);
        return record[..keep] + tail;
    }

    // ── FR-05: cho người dùng biết ở lần mở kế tiếp ─────────────────────────
    public readonly record struct Incident(string Path, string Time, int Pid, string Session,
                                           string Verdict, string TopBreadcrumb)
    {
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;
    }

    /// <summary>
    /// Chọn hồ sơ của LẦN CHẠY TRƯỚC. "Trước" được định nghĩa bằng session tag KHÁC phiên hiện tại,
    /// không bằng mốc thời gian: Windows dựng lại PID và hai phiên có thể mở trong cùng một phút.
    /// Trả null khi chưa có gì, hoặc khi file không đọc được — việc thông báo không bao giờ được
    /// làm hỏng lúc khởi động.
    /// </summary>
    public static Incident? FindPreviousIncident(string capturesDir, string currentSessionTag)
    {
        try
        {
            if (!Directory.Exists(capturesDir)) return null;
            foreach (var f in new DirectoryInfo(capturesDir).GetFiles("hang-*.txt")
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var inc = ParseRecord(f.FullName, currentSessionTag);
                if (inc is not null) return inc;
            }
        }
        catch { }
        return null;
    }

    public static Incident? ParseRecord(string path, string currentSessionTag)
    {
        try
        {
            string time = "", verdict = "", crumb = "", session = "";
            int pid = 0;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("pid="))
                {
                    // Dòng header thật có dạng: "pid=123 session=ab12cd time=2026-11-01 14:52:07.123"
                    // ⇒ time nằm TRONG dòng này, không phải dòng riêng.
                    var tokens = line.Split(' ');
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        var tok = tokens[i];
                        if (tok.StartsWith("pid=")) int.TryParse(tok[4..], out pid);
                        else if (tok.StartsWith("session=")) session = tok[8..];
                        else if (tok.StartsWith("time=") && time.Length == 0)
                        {
                            var parts = new List<string> { tok[5..] };
                            if (i + 1 < tokens.Length) parts.Add(tokens[i + 1]);   // "14:52:07.123"
                            time = string.Join(" ", parts);
                        }
                    }
                }
                else if (line.StartsWith("time=") && time.Length == 0) time = line[5..];
                else if (line.StartsWith("verdict=") && verdict.Length == 0) verdict = line[8..];
                else if (crumb.Length == 0 && line.StartsWith("[") && line.Contains('@')) crumb = line;
            }

            if (verdict.Length == 0) return null;                     // không phải hồ sơ treo
            if (session.Length > 0 && session == currentSessionTag) return null;   // của chính phiên này
            return new Incident(path, time, pid, session, verdict, crumb);
        }
        catch { return null; }
    }

    /// <summary>
    /// Nội dung hiện cho người dùng. FR-05 đòi "chỉ nơi lưu bằng chứng", nên đường dẫn đầy đủ
    /// phải nằm trong text — không phải kiểu "xem trong thư mục AppData".
    /// </summary>
    public static string BuildNotice(Incident inc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ứng dụng đã dừng đột ngột ở lần chạy trước vì giao diện không phản hồi.");
        sb.AppendLine($"Thời điểm: {(inc.Time.Length > 0 ? inc.Time : "?")}   PID: {(inc.Pid > 0 ? inc.Pid.ToString() : "?")}");
        if (inc.Verdict.Length > 0) sb.AppendLine($"Lý do ghi được: {inc.Verdict}");
        if (inc.TopBreadcrumb.Length > 0) sb.AppendLine($"Thao tác đang chạy: {inc.TopBreadcrumb}");
        sb.AppendLine();
        sb.Append($"Hồ sơ đầy đủ: {inc.Path}");
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_loop is not null && _loop.IsAlive) _loop.Join(1000);
    }
}
