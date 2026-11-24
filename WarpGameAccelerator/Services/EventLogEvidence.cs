// ============================================================
// Services/EventLogEvidence.cs
// Thu thap bang chung tu Event Log CUA MAY DANG CHAY (#35, batch 53).
// ============================================================
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WarpGameAccelerator.Services;

/// <summary>Một bản ghi bằng chứng từ Event Log của máy đang chạy: Application Hang (1002),
/// Application Error (1000), hoặc kenh `.NET Runtime` (1025 = FailFast cua watchdog,
/// 1026 = exception chưa được xử lý).</summary>
public sealed record AppEventRecord(bool IsCrash, DateTime TimeUtc, string Version, string ExePath,
                                    DateTime? BornUtc, string Detail,
                                    bool IsRuntime = false, string? Message = null);

public static class EventLogEvidence
{
    // Duong dan cua app xuat hien trong log Windows — nguoi dung KHONG nen nhan duoc ten may/ten
    // user, nen moi duong dan deu phai rut gon qua Flavor() truoc khi toi day.
    private const string AppName = "WarpGameAccelerator";

    /// <summary>Ket qua doc: Items + Error. KHONG tra danh sach rong im lang — "0" cua mot kenh hong
    /// khong phai la so lieu (bai hoc batch 36/47, lan nay chinh lab E12 bat).</summary>
    public readonly record struct ReadResult(List<AppEventRecord> Items, string? Error);

    public static ReadResult ReadLocalChecked(int days = 30)
    {
        var list = new List<AppEventRecord>();
        if (days <= 0) return new ReadResult(list, "days <= 0");
        try
        {
            long ms = (long)days * 86_400_000L;
            var xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {ms}] and " +
                        $"((EventID=1002) or (EventID=1000) or " +
                        $"(Provider[@Name='.NET Runtime'] and ((EventID=1025) or (EventID=1026))))]]";
            int seen = 0;
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            for (EventRecord? rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
            {
                seen++;
                var parsed = Parse(rec);
                if (parsed is not null) list.Add(parsed);
            }
            if (seen == 0) return new ReadResult(list, "kenh song nhung 0 ban ghi 1000/1002 trong cua so");
            if (list.Count == 0) return new ReadResult(list, $"doc duoc {seen} ban raw nhung parse 0 — ten truong/XML doi, phai sua cong cu");
            return new ReadResult(list, null);
        }
        catch (Exception ex)
        {
            // Kenh hong (quyen / log bi khoa): bao loi van gui duoc, chi la khong co khoi nay.
            return new ReadResult(new List<AppEventRecord>(), ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static IReadOnlyList<AppEventRecord> ReadLocal(int days = 30) => ReadLocalChecked(days).Items;

    /// <summary>Tach XML → record. Tam ly cua batch 52: truong can nam trong <c>EventData</c>,
    /// KHONG phai trong doan message text.</summary>
    internal static AppEventRecord? Parse(EventRecord rec)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(rec.ToXml());
            // ToXml() dat moi node trong xmlns='...events/event'. XPath KHONG có namespace manager thi
            // tra ve null cho MOI node -> Parse loai tham toan bo 52 ban ghi (lab E12b bat duoc,
            // 73 ms, khong ném). Day la loi "cong cu cam", khong phai "khong co du lieu".
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
            var data = doc.SelectSingleNode("/e:Event/e:EventData", ns);
            if (data is null) return null;

            string Get(string name) =>
                data.SelectSingleNode($"e:Data[@Name='{name}']", ns)?.InnerText ?? string.Empty;

            // Kenh `.NET Runtime` (1025/1026) KHONG dung Data[@Name]: EventData chi la MOT khoi text.
            // Phep thu co che (do that 2026-11-01): 1002/1000 = 8-11 Data co ten; 1025/1026 = 1 Data tran.
            var named = data.SelectNodes("e:Data[@Name]", ns);
            if (named is null || named.Count == 0) return ParseRuntimeBlob(doc, ns, rec);

            string exe = Get("ExeFileName");
            if (exe.Length == 0) exe = Get("AppPath");                 // 1000 dung ten khac
            if (!exe.Contains(AppName, StringComparison.OrdinalIgnoreCase))
            {
                // 1000 cua app khong co ExeFileName thi con AppName; 1002 luon co duong dan.
                string app = Get("AppName");
                if (!app.Contains(AppName, StringComparison.OrdinalIgnoreCase)) return null;
            }

            DateTime born = ParseFiletime(Get("StartTime").Length > 0 ? Get("StartTime") : Get("ProcessCreationTime"));
            int id = rec.Id;
            string detail = id == 1002
                ? Get("HangType")
                : $"{Get("ModuleName")} @ {Get("FaultingOffset")} / {Get("ExceptionCode")}";
            return new AppEventRecord(
                IsCrash: id == 1000,
                TimeUtc: rec.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                Version: Get("AppVersion"),
                ExePath: exe,
                BornUtc: born == default ? null : born,
                Detail: detail);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>FILETIME (100 ns tu 1601, UTC) → DateTime UTC. Sai o day chinh la loi batch 51:
    /// tru mot DateTime UTC lay tu DateTime naive gio dia phuong lech nguyen mot múi gio.</summary>
    internal static DateTime ParseFiletime(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return default;
        if (!hex.StartsWith("0x")) return default;
        if (!long.TryParse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long ft))
            return default;
        // 1 tick .NET = 100 ns = dung 1 don vi FILETIME -> KHONG duoc chia them 100 (loi E8:
        // chia nham lam median tuoi phien thanh 421 nam, du build 0 error).
        return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ft);
    }

    /// <summary>1025/1026 của provider <c>.NET Runtime</c>: EventData chỉ là MỘT khối text
    /// (`Application:` / `Description:` / `Message:` / các dòng stack `   at ...`). Đo trên XML thật
    /// (batch 54): kenh nay ghi cho 17 tiến trình khac trong 3 thang va 169/192 ban ghi CO stack
    /// managed; 1025 cua failfast-probe chua dung record cua watchdog (= duong van chuyen ho so
    /// toi nguoi dung). App moi co 1 ban 1026, khong stack — vi e0434e49 den tu tang native.</summary>
    internal static AppEventRecord? ParseRuntimeBlob(XmlDocument doc, XmlNamespaceManager ns, EventRecord rec)
    {
        var node = doc.SelectSingleNode("/e:Event/e:EventData/e:Data", ns);
        if (node is null) return null;
        return ParseRuntimeBlobText(node.InnerText ?? string.Empty, rec.Id,
                                    rec.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow);
    }

    /// <summary>Bien thep cua ParseRuntimeBlob: vao text + id + thoi gian, khong can EventRecord
    /// (EventRecord khong co ham khoi dong cong khai) ⇒ lab kiem duoc bang fixture lay troc tu
    /// XML that cua ban 1025 ghi ngay 01/11.</summary>
    internal static AppEventRecord? ParseRuntimeBlobText(string blob, int eventId, DateTime timeUtc)
    {
        if (blob.Length == 0) return null;

        string Line(string prefix) =>
            blob.Split('\n').Select(s => s.TrimEnd('\r'))
                .FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                is string hit ? hit[prefix.Length..].Trim() : string.Empty;

        string app = Line("Application:");
        if (!app.Contains(AppName, StringComparison.OrdinalIgnoreCase)) return null;

        bool hasStack = Regex.IsMatch(blob, @"(?m)^\s+at\s");
        string info = Line("Exception Info:");
        return new AppEventRecord(
            IsCrash: eventId == 1026,
            TimeUtc: timeUtc,
            Version: Line(".NET Version:"),
            ExePath: app,
            BornUtc: null,                                   // kenh nay khong cap Timestamp sinh tien trinh
            Detail: hasStack ? "co stack managed" : (info.Length > 0 ? info : "khong co stack"),
            IsRuntime: true,
            Message: ScrubPaths(Line("Message:")));
    }

    /// <summary>Stack/Message của kenh .NET Runtime có thể chứa đường dẫn tuyệt đối — gặp thật:
    /// `... in C:\Users\…\AppData\Local\Temp\WarpNtuProbe\Program.cs:line 45`. Bắt buộc tẩy home
    /// trước khi khối này rời máy người dùng.</summary>
    internal static string ScrubPaths(string text)
    {
        if (text.Length == 0) return text;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? text : text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Duong dan → chuoi AN TOAN de vao bao loi: flavor + duong dan da cat home.
    /// Luu y (lab E4): ban 1000 cua app mo bang <c>dotnet run</c> co AppPath la <c>dotnet.exe</c>
    /// chu KHONG phai duong dan app — gan nham no thanh "installed" se nau sach thong ke
    /// dev-vs-nguoi-dung, dung cai loai loi batch 43 mac phai.</summary>
    internal static string Flavor(string exePath, string? userHome = null)
    {
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = exePath;
        if (!string.IsNullOrEmpty(userHome) && p.StartsWith(userHome, StringComparison.OrdinalIgnoreCase))
            p = "~" + p[userHome.Length..];
        if (!p.Contains(AppName, StringComparison.OrdinalIgnoreCase))
            return $"host exe={p}";
        // Kenh .NET Runtime chi ghi TEN tien trinh, khong ghi duong dan → khong duoc do thanh
        // "installed" (se nau ty le dev/user cua #35).
        if (!p.Contains("\\") && !p.Contains("/")) return "runtime-khong-duong-dan exe=" + p;
        bool dev = p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
        string flavor = !dev ? "installed"
                      : p.Contains("Debug", StringComparison.OrdinalIgnoreCase) ? "DEV-Debug"
                      : p.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "DEV-Release"
                      : "DEV-?";
        return $"{flavor} exe={p}";
    }

    /// <summary>Khoi van ban cho bao loi. Ham THUAN (du lieu truyen vao) de kiem chung duoc tren
    /// du lieu that ma khong can doc log cua may dang chay.</summary>
    public static string Summarize(IReadOnlyList<AppEventRecord> records, string? userHome = null, int maxChars = 320)
    {
        if (records.Count == 0) return string.Empty;

        int hangs = records.Count(r => !r.IsCrash && !r.IsRuntime);
        int crashes = records.Count(r => r.IsCrash && !r.IsRuntime);
        int runtime = records.Count(r => r.IsRuntime);
        int withStack = records.Count(r => r.IsRuntime && r.Detail.Contains("stack managed"));
        var ages = records.Where(r => r.BornUtc is not null)
                          .Select(r => Math.Max(0, (r.TimeUtc - r.BornUtc!.Value).TotalSeconds))
                          .OrderBy(a => a).ToList();
        // Median theo quy chuan (chan dua giua voi so chan) — cung quy uoc voi phep phan tich
        // Python o batch 52 (st.median), de hai ben khong bao hai con so khac nhau ve cung tap.
        double medianAge = ages.Count == 0 ? -1
            : ages.Count % 2 == 1 ? ages[ages.Count / 2]
            : (ages[ages.Count / 2 - 1] + ages[ages.Count / 2]) / 2.0;

        var sb = new StringBuilder();
        sb.Append($"[Event Log may nay] {hangs} treo / {crashes} crash trong cua so do");
        if (runtime > 0)
            sb.Append($"; kenh .NET Runtime: {runtime} (co stack managed: {withStack})");
        if (ages.Count > 0)
            sb.Append($"; tuoi phien median {FormatAge(medianAge)} (n={ages.Count})");

        // Phan loai: AppVersion co the la phien ban .NET HOST (batch 52 phat hien: 8.0.2926.32403 xuat
        // hien khi mo bang dotnet.exe) — dem rieng de khong gia that so phien ban cua app.
        string hostPrefix = $"{Environment.Version.Major}.";
        // Ban .NET Runtime KHONG chua version cua app (chi co ".NET Version" = version runtime),
        // nen loai khoi bo dem version — neu khong thi "17 phien ban app" bi mot ban 1026 chen them
        // so gia (phat hien qua lab E17).
        var versioned = records.Where(r => !r.IsRuntime).ToList();
        var appVersions = versioned.Select(r => r.Version)
                                 .Where(v => v.Length > 0 && !v.StartsWith(hostPrefix, StringComparison.Ordinal))
                                 .Distinct().Count();
        var hostVersions = versioned.Count(r => r.Version.StartsWith(hostPrefix, StringComparison.Ordinal));
        sb.Append($"; phien ban app: {appVersions}");
        if (hostVersions > 0) sb.Append($" (+{hostVersions} ban ghi mang version HOST, khong dem)");

        var fl = records.Select(r => Flavor(r.ExePath, userHome).Split(' ')[0])
                        .GroupBy(f => f).OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Key}×{g.Count()}").Take(3);
        sb.Append($"; nguon chay: {string.Join(", ", fl)}");

        string outText = sb.ToString();
        if (outText.Length > maxChars)
        {
            const string tail = "…(cắt)";
            outText = outText[..Math.Max(0, maxChars - tail.Length)] + tail;
        }
        return outText;
    }

    // Luon kem giay (invariant) de chuoi do MAY kiem chung duoc — lab E5 doi chieu thang con so
    // nay voi median tinh bang Python tren cung tap CSV (batch 52).
    private static string FormatAge(double seconds) =>
        seconds < 90 ? $"{seconds.ToString("F0", CultureInfo.InvariantCulture)} s"
        : seconds < 5400 ? $"{(seconds / 60).ToString("F1", CultureInfo.InvariantCulture)} phút ({seconds.ToString("F0", CultureInfo.InvariantCulture)} s)"
        : $"{(seconds / 3600).ToString("F1", CultureInfo.InvariantCulture)} giờ ({seconds.ToString("F0", CultureInfo.InvariantCulture)} s)";
}
