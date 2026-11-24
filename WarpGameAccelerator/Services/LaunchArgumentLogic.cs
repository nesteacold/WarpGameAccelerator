// ============================================================
// Services/LaunchArgumentLogic.cs
// Logic THUẦN (không P/Invoke, không log, không đụng tiến trình thật) cho
// "argument khởi động" của fxgame.exe — chuỗi dòng lệnh do fxlaunch.exe sinh
// ra theo phiên đăng nhập. Trong code/JSON cũ trường này vẫn tên là "Token"
// (GameFolderEntry.Token) để giữ tương thích game_folders.json.
//
// Tách riêng để lab experiments/launch-argument biên dịch được CHÍNH mã này mà
// không kéo theo DiagnosticLogService (lab không được ghi vào trace.log thật —
// xem CLAUDE.md, cổng G14).
//
// Sự cố gốc (2026-11-15, FCT-2658): xoá rồi thêm lại thư mục D:\AOW_Dev, Booster
// bắt argument từ fxgame.exe của BẤT KỲ thư mục nào đang chạy, lưu nhầm; client
// mang argument sai CRASH sớm (WER treo gần hết thread — FCT-2657: 53/54 thread
// Wait/Suspended) rồi biến mất ~60 s sau khi mở, trong khi kiểm "còn sống sau
// 8 s" luôn đạt ⇒ Booster nhân bản client hỏng mãi.
//
// Nguyên tắc phán quyết (review Codex DEC-598 vòng 1):
//  - TÍN HIỆU THẬT của argument sai là TIẾN TRÌNH CHẾT / bị WER treo trong cửa sổ
//    quan sát. Không thấy kết nối công thì KHÔNG đủ để kết luận hỏng (proxy cục bộ,
//    proxy trên máy khác trong LAN, mạng chậm đều cho ra cùng hình) ⇒ Unknown.
//  - Một ESTAB công KHÔNG đủ để xác nhận: argument hết hạn vẫn có thể mở TCP tới
//    máy chủ đăng nhập trước khi bị từ chối ⇒ Valid cần sống trọn cửa sổ.
// ============================================================
using System.Security.Cryptography;
using System.Text;

namespace WarpGameAccelerator.Services;

public enum ArgumentVerdict
{
    /// <summary>Client sống trọn cửa sổ quan sát, không bị WER treo, và đã có kết nối công.</summary>
    Valid,
    /// <summary>Client chết hoặc bị WER treo trong cửa sổ quan sát ⇒ argument coi như hết hạn.</summary>
    Stale,
    /// <summary>Còn sống nhưng không chứng minh được gì (proxy LAN/cục bộ, mạng chậm) ⇒ không tắt,
    /// không làm mới, chỉ log.</summary>
    Unknown
}

/// <summary>Tóm tắt một lần quan sát client vừa mở bằng argument đang xét.</summary>
/// <param name="Died">Tiến trình thoát trong cửa sổ.</param>
/// <param name="SustainedWerSuspend">Gần như mọi thread bị Suspended qua nhiều mẫu liên tiếp (WER đang xử lý crash).</param>
/// <param name="SawPublicConnection">Từng thấy ESTAB tới địa chỉ công cộng.</param>
/// <param name="AliveThroughWindow">Còn sống, không bị WER treo, khi hết trọn cửa sổ.</param>
public sealed record LaunchObservation(bool Died, bool SustainedWerSuspend, bool SawPublicConnection, bool AliveThroughWindow);

/// <summary>
/// Gom các mẫu của MỘT lần quan sát (thuần — vòng poll thật nằm ở MultiClientService). WER-treo chỉ
/// tính khi đủ <see cref="LaunchArgumentLogic.WerSuspendSustainSamples"/> mẫu LIÊN TIẾP; mẫu không
/// đọc được (null) làm đứt chuỗi (không biết thì không cộng dồn thành bằng chứng).
/// </summary>
public sealed class LaunchObserver
{
    private int _werRun;
    public bool Died { get; private set; }
    public bool SustainedWerSuspend { get; private set; }
    public bool SawPublicConnection { get; private set; }

    /// <summary>Đã đủ bằng chứng Stale — dừng chờ sớm.</summary>
    public bool ShouldStopEarly => Died || SustainedWerSuspend;

    public void Sample(bool alive, bool? werSuspended, bool publicConnectionNow)
    {
        if (!alive) { Died = true; return; }
        if (publicConnectionNow) SawPublicConnection = true;
        if (werSuspended == true)
        {
            if (++_werRun >= LaunchArgumentLogic.WerSuspendSustainSamples) SustainedWerSuspend = true;
        }
        else _werRun = 0;
    }

    /// <summary>Kết thúc cửa sổ: còn sống và mẫu cuối không WER-treo ⇒ AliveThroughWindow.</summary>
    public LaunchObservation Finish(bool aliveAtEnd, bool? werSuspendedAtEnd)
    {
        if (!aliveAtEnd) Died = true;
        bool through = aliveAtEnd && !Died && !SustainedWerSuspend && werSuspendedAtEnd != true;
        return new LaunchObservation(Died, SustainedWerSuspend, SawPublicConnection, through);
    }
}

/// <summary>Ảnh chụp một fxgame.exe của MỘT thư mục, dùng cho quyết định tự cập nhật (D).</summary>
public sealed record FolderClientSnapshot(int Pid, DateTime StartTime, string Argument, bool WerSuspended);

public static class LaunchArgumentLogic
{
    /// <summary>
    /// Cửa sổ quan sát client mở bằng argument CHƯA được xác nhận. Đo FCT-2657 (pid 46180): mở
    /// 06:34:30, WER treo thread đã thấy lúc 06:34:37, WER\Temp sinh 06:35:27 (+57 s), tiến trình biến
    /// mất ~60 s sau khi mở ⇒ 70 s phủ được mốc chết, dư 10 s.
    /// </summary>
    public const int ObserveWindowMs = 70_000;

    /// <summary>
    /// Client "ổn định" cho tự cập nhật (D): sống ít nhất chừng này, không bị WER treo. Client mang
    /// argument sai chết ~60 s (FCT-2657) ⇒ 3 phút là gấp 3 lần mốc đó.
    /// </summary>
    public static readonly TimeSpan StableClientAge = TimeSpan.FromMinutes(3);

    /// <summary>Số mẫu WER-treo LIÊN TIẾP (mỗi ~2 s) mới kết luận. Một mẫu đơn lẻ có thể xảy ra
    /// trong lúc tiến trình vừa được khởi tạo.</summary>
    public const int WerSuspendSustainSamples = 3;

    /// <summary>
    /// exe có phải CHÍNH fxgame.exe của thư mục game này không — so KHỚP ĐÚNG đường dẫn sau chuẩn
    /// hoá (không phân biệt hoa thường), KHÔNG so tiền tố (Codex vòng 2 P1#1: so tiền tố để lọt
    /// "D:\AOW_Dev\Test\bin64\fxgame.exe" làm ứng viên của "D:\AOW_Dev"). Tập vị trí hợp lệ đóng:
    /// &lt;folder&gt;\fxgame.exe, &lt;folder&gt;\bin64\fxgame.exe, &lt;folder&gt;\bin\fxgame.exe (đo 2026-11-16 trên
    /// D:\AOW_Dev và D:\AOW_TW: cả bin\ (i386) lẫn bin64\ (AMD64) đều có fxgame.exe — CLAUDE.md), cộng
    /// <paramref name="findGameExePath"/> = đúng đường dẫn FindGameExe trả cho thư mục đó (nó có thể
    /// đào 2 cấp khi bản cài lạ).
    /// </summary>
    public static bool IsGameExeOfFolder(string exePath, string folder, string? findGameExePath = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var fullExe = Path.GetFullPath(exePath);
            foreach (var allowed in GameExeLocations(folder, findGameExePath))
                if (fullExe.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Thư mục (trong danh sách đã cấu hình) SỞ HỮU exe này — cùng quy tắc khớp ĐÚNG đường dẫn với
    /// <see cref="IsGameExeOfFolder"/> (Codex v3 P1#1: bản cũ so tiền tố và lấy thư mục khớp ĐẦU TIÊN,
    /// nên thêm D:\AOW_Dev trước D:\AOW_Dev\Test thì client của Test thuộc dòng cha, và "Tắt tất cả"
    /// dòng cha giết luôn nó). null = không thư mục nào sở hữu (bucket "không rõ thư mục").
    /// <paramref name="findGameExeByFolder"/>: đường dẫn FindGameExe trả cho từng thư mục (có thể thiếu).
    /// </summary>
    public static string? ResolveOwningFolder(string exePath, IReadOnlyList<string> knownFolders,
        IReadOnlyDictionary<string, string?>? findGameExeByFolder = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        foreach (var folder in knownFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string? found = null;
            findGameExeByFolder?.TryGetValue(folder, out found);
            if (IsGameExeOfFolder(exePath, folder, found)) return folder;
        }
        return null;
    }

    /// <summary>Các đường dẫn fxgame.exe hợp lệ (đã chuẩn hoá) của một thư mục game.</summary>
    public static List<string> GameExeLocations(string folder, string? findGameExePath = null)
    {
        var result = new List<string>();
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (root.Length == 0) return result;
            result.Add(Path.Combine(root, "fxgame.exe"));
            result.Add(Path.Combine(root, "bin64", "fxgame.exe"));
            result.Add(Path.Combine(root, "bin", "fxgame.exe"));
            if (!string.IsNullOrWhiteSpace(findGameExePath))
                result.Add(Path.GetFullPath(findGameExePath));
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Thứ tự PID được phép xét cho MỘT thư mục: chỉ PID có exe thuộc thư mục đó; PID mới (không
    /// có trong <paramref name="pidsBefore"/>) đứng trước, mới khởi động trước; <paramref name="newOnly"/>
    /// = true thì bỏ hẳn PID cũ. PID không đọc được đường dẫn (không có trong
    /// <paramref name="exePathsByPid"/>) không bao giờ được chọn.
    /// </summary>
    public static List<int> OrderCandidatePids(
        IReadOnlyDictionary<int, string> exePathsByPid, string folder,
        IReadOnlyCollection<int>? pidsBefore, bool newOnly,
        IReadOnlyDictionary<int, DateTime>? startTimes = null, string? findGameExePath = null)
    {
        var inFolder = exePathsByPid
            .Where(kv => IsGameExeOfFolder(kv.Value, folder, findGameExePath))
            .Select(kv => kv.Key)
            .ToList();

        DateTime Start(int pid) =>
            startTimes != null && startTimes.TryGetValue(pid, out var t) ? t : DateTime.MinValue;

        IEnumerable<int> Newest(IEnumerable<int> pids) =>
            pids.OrderByDescending(Start).ThenByDescending(p => p);

        var fresh = inFolder.Where(p => pidsBefore == null || !pidsBefore.Contains(p)).ToList();
        var result = Newest(fresh).ToList();
        if (!newOnly)
            result.AddRange(Newest(inFolder.Except(fresh)));
        return result;
    }

    /// <summary>
    /// Chọn PID của ĐÚNG lần mở vừa rồi trong các PID mới của thư mục (đã lọc + xếp bởi
    /// <see cref="OrderCandidatePids"/>): ưu tiên PID có argument trùng argument vừa truyền; PID
    /// đọc được argument mà KHÁC thì không bao giờ được chọn (đó là lần mở của ai khác); PID
    /// không đọc được dòng lệnh (null — khác mức quyền) chỉ là phương án cuối. 0 = chưa thấy.
    /// </summary>
    public static int SelectLaunchedPid(IReadOnlyList<int> newFolderPidsOrdered,
        IReadOnlyDictionary<int, string?> argumentByPid, string expectedArgument)
    {
        foreach (var pid in newFolderPidsOrdered)
            if (argumentByPid.TryGetValue(pid, out var a) && a != null &&
                string.Equals(a, expectedArgument, StringComparison.Ordinal))
                return pid;
        foreach (var pid in newFolderPidsOrdered)
            if (!argumentByPid.TryGetValue(pid, out var a) || a == null)
                return pid;
        return 0;
    }

    /// <summary>
    /// Argument khởi động = từ đầu tiên sau đường dẫn exe trong dòng lệnh.
    /// Dạng: "C:\Path\fxgame.exe" ARG  hoặc  C:\Path\fxgame.exe ARG.
    /// </summary>
    public static string ParseArgument(string cmdLine)
    {
        if (string.IsNullOrEmpty(cmdLine)) return string.Empty;
        cmdLine = cmdLine.Trim();
        string rest;

        if (cmdLine.StartsWith("\""))
        {
            int closing = cmdLine.IndexOf('"', 1);
            if (closing < 0) return string.Empty;
            rest = cmdLine[(closing + 1)..].Trim();
        }
        else
        {
            int space = cmdLine.IndexOf(' ');
            if (space < 0) return string.Empty;
            rest = cmdLine[(space + 1)..].Trim();
        }

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    /// <summary>sha256 (UTF-8) cắt 8 ký tự hex — đủ để so hai argument trong log mà không lộ giá trị.</summary>
    public static string ShortHash(string argument)
    {
        if (string.IsNullOrEmpty(argument)) return "rỗng";
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(argument));
        return Convert.ToHexString(h).ToLowerInvariant()[..8];
    }

    /// <summary>Mô tả an toàn để log/hiển thị: CHỈ độ dài + hash ngắn, không bao giờ là giá trị.</summary>
    public static string Describe(string argument) =>
        string.IsNullOrEmpty(argument)
            ? "rỗng"
            : $"độ dài={argument.Length}, sha256[:8]={ShortHash(argument)}";

    /// <summary>
    /// Một mẫu thread có phải hình "WER đang xử lý crash" không: gần như MỌI thread Wait/Suspended
    /// (FCT-2657: 53/54, FCT-2656: 48/50 — thread còn lại chờ WerFault qua ALPC). Đòi tối thiểu 8
    /// thread để loại lúc tiến trình vừa tạo bằng CREATE_SUSPENDED (1 thread, treo có chủ đích).
    /// </summary>
    public static bool IsWerSuspendedSample(int totalThreads, int suspendedThreads) =>
        totalThreads >= 8 && suspendedThreads > 0 && suspendedThreads >= totalThreads - 2;

    /// <summary>
    /// Phán quyết theo quan sát. Stale CHỈ khi tiến trình chết hoặc bị WER treo kéo dài — dấu hiệu đã
    /// đo được của argument sai. Valid cần sống trọn cửa sổ VÀ đã có kết nối công (một ESTAB sớm
    /// không đủ: argument hết hạn vẫn có thể nối máy chủ đăng nhập trước khi bị từ chối). Mọi ca
    /// còn lại ⇒ Unknown (không tắt, không làm mới).
    /// </summary>
    public static ArgumentVerdict ClassifyObservation(LaunchObservation o)
    {
        if (o.Died || o.SustainedWerSuspend) return ArgumentVerdict.Stale;
        if (o.AliveThroughWindow && o.SawPublicConnection) return ArgumentVerdict.Valid;
        return ArgumentVerdict.Unknown;
    }

    /// <summary>
    /// (C) Client MỚI vừa mở có phải quan sát trọn cửa sổ không. Chỉ được bỏ quan sát khi một client
    /// MỚI của CÙNG lần bấm (client nguồn từ launcher, hoặc client mới đầu tiên của lượt) đã ra
    /// Valid. KHÔNG phụ thuộc vị trí trong lượt (vòng 1: client cuối lọt — Codex v1 P1#4); KHÔNG nhận
    /// client phiên CŨ làm bằng chứng (Codex v2 P1#4: argument hết hạn cho đăng nhập mới nhưng phiên
    /// cũ vẫn sống); Unknown cũng KHÔNG cho bỏ quan sát client kế tiếp (chỉ sau Valid).
    /// </summary>
    public static bool ShouldVerifyClient(bool newClientValidThisClick) => !newClientValidThisClick;

    /// <summary>
    /// Mốc (watermark) cho tự cập nhật sau một lượt mở: StartTime của client NGUỒN đã sinh/xác nhận
    /// argument đang lưu — KHÔNG phải lúc ghi (Codex v1 P2#6). null = không biết. Argument đổi ⇒ lấy
    /// mốc nguồn mới (kể cả null). Không đổi ⇒ mốc không bao giờ lùi. Mốc được LƯU vào
    /// game_folders.json (Codex v2 P1#2) để sống qua lần khởi động lại Booster.
    /// </summary>
    public static DateTime? NextWatermark(DateTime? current, DateTime? sourceStart, bool argumentChanged)
    {
        if (argumentChanged) return sourceStart;
        if (sourceStart == null) return current;
        return current == null || sourceStart > current ? sourceStart : current;
    }

    /// <summary>
    /// (B, Codex v2 P1#3) Có được NHẬN argument do launcher vừa sinh không, theo phán quyết quan sát
    /// client NGUỒN của nó: Stale (chết / WER treo) ⇒ KHÔNG (không lưu, không nhân bản, báo lỗi);
    /// Valid / Unknown ⇒ có (Unknown: argument do chính launcher sinh, không có bằng chứng hỏng — các
    /// client sau vẫn bị quan sát vì <see cref="ShouldVerifyClient"/> chỉ bỏ sau Valid).
    /// </summary>
    public static bool AcceptLauncherSource(ArgumentVerdict sourceVerdict) => sourceVerdict != ArgumentVerdict.Stale;

    /// <summary>
    /// (Codex v3 P1#2) Một lần gọi helper chỉ được TÍNH là "đã mở" khi tìm được PID của chính lần mở đó
    /// (đúng thư mục, đúng argument). Bản cũ tăng số đếm TRƯỚC khi tìm PID ⇒ client chết trước lần poll
    /// đầu / xuất hiện muộn vẫn được tính, cuối cùng báo "đã mở đủ".
    /// </summary>
    public static bool CountsAsLaunched(int newPid) => newPid > 0;

    /// <summary>
    /// (Codex v3 P1#2) Không tìm được PID của lần mở mà argument CHƯA được xác nhận bởi một client mới
    /// của cùng lần bấm ⇒ coi như không xác nhận được ⇒ dừng lượt (không nhân bản tiếp bằng argument
    /// chưa kiểm). Đã xác nhận thì chỉ ghi lỗi cho lần mở đó và mở tiếp.
    /// </summary>
    public static bool ShouldStopOnMissingPid(bool newClientValidThisClick) => !newClientValidThisClick;

    /// <summary>
    /// (Codex v3 P1#2) Chỉ báo thành công khi số client THỰC TẾ của thư mục (đếm lại sau lượt) đạt mục
    /// tiêu và lượt không bị dừng — không bao giờ dựa vào số lần gọi helper.
    /// </summary>
    public static bool IsRoundSuccess(int actualRunningInFolder, int targetTotal, bool roundStopped) =>
        !roundStopped && actualRunningInFolder >= targetTotal;

    /// <summary>
    /// (C) Dừng mở thêm client khi client vừa mở bằng argument hiện tại bị phán Stale VÀ argument
    /// chưa được xác nhận trong lượt — tiếp tục chỉ nhân bản client hỏng.
    /// </summary>
    public static bool ShouldStopLaunching(ArgumentVerdict lastClient, bool argumentConfirmed) =>
        lastClient == ArgumentVerdict.Stale && !argumentConfirmed;

    /// <summary>Client sống đủ lâu và không bị WER treo.</summary>
    public static bool IsStableClient(DateTime startTime, DateTime now, bool werSuspended, TimeSpan minAge) =>
        !werSuspended && now - startTime >= minAge;

    /// <summary>
    /// (D) Chọn argument để tự cập nhật: trong các client của thư mục khởi động SAU
    /// <paramref name="watermark"/> (StartTime của client NGUỒN của argument đang lưu — Codex v1
    /// P2#6), ổn định (<see cref="IsStableClient"/>, bất kể kiểu kết nối — Codex v1 P1#5), lấy client
    /// MỚI NHẤT; null nếu không có hoặc argument trùng argument đã lưu.
    /// <paramref name="clients"/> phải là MỌI client đang chạy của thư mục đọc được argument (kể cả
    /// client trẻ / bị WER treo): khi <paramref name="watermark"/> = null (file cũ chưa có mốc / nguồn
    /// không rõ) thì KHÔNG ghi đè nếu còn BẤT KỲ client nào chạy bằng đúng argument đang lưu (Codex
    /// v2 P1#2: restart Booster làm mất mốc ⇒ client phiên cũ ghi đè argument mới).
    /// </summary>
    public static FolderClientSnapshot? PickArgumentUpdate(IEnumerable<FolderClientSnapshot> clients,
        string savedArgument, DateTime? watermark, DateTime now, TimeSpan minAge)
    {
        var list = clients.ToList();
        if (watermark == null && !string.IsNullOrEmpty(savedArgument) &&
            list.Any(c => string.Equals(c.Argument, savedArgument, StringComparison.Ordinal)))
            return null;
        var mark = watermark ?? DateTime.MinValue;
        var newest = list
            .Where(c => !string.IsNullOrEmpty(c.Argument) && c.StartTime > mark &&
                        IsStableClient(c.StartTime, now, c.WerSuspended, minAge))
            .OrderByDescending(c => c.StartTime)
            .ThenByDescending(c => c.Pid)
            .FirstOrDefault();
        if (newest == null) return null;
        return string.Equals(newest.Argument, savedArgument ?? string.Empty, StringComparison.Ordinal) ? null : newest;
    }
}
