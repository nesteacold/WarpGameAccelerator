// ============================================================
// Services/MultiClientService.cs
// Quản lý việc mở nhiều client game AOW (Age of Wushu)
// ============================================================
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WarpGameAccelerator.Services;

public class AowTokenInfo
{
    public string Token       { get; set; } = string.Empty;
    // LEGACY (trước v-nhiều-thư-mục): thư mục game duy nhất từng lưu kèm
    // token. Từ khi có game_folders.json (danh sách nhiều thư mục), field
    // này KHÔNG còn được ghi mới — chỉ giữ lại để đọc file cũ phục vụ
    // migration một lần (xem MultiClientService.LoadGameFolders).
    public string GameFolder  { get; set; } = string.Empty;
    public string SavedAt     { get; set; } = string.Empty;
}

/// <summary>
/// Một thư mục cài đặt game đã biết (tự quét được hoặc người dùng thêm tay).
/// Persist trong Data\game_folders.json — thay thế field GameFolder đơn lẻ
/// cũ trong aow_token.json để hỗ trợ nhiều thư mục cùng lúc.
///
/// Token TỪNG là account-level (dùng chung mọi thư mục) — đã đổi thành
/// PER-FOLDER: mỗi thư mục là một tài khoản/phiên đăng nhập riêng (lấy bằng
/// cách mở fxlaunch.exe của CHÍNH thư mục đó lần đầu, đợi fxgame.exe chạy,
/// rồi đọc token từ command line của nó — xem EnsureClientsRunningAsync/
/// WaitForTokenInternalAsync). Token cũ lưu ở aow_token.json không được
/// migrate sang đây vì không rõ token đó thuộc thư mục nào.
/// </summary>
public record GameFolderEntry(string Path, int LastClientCount, DateTimeOffset AddedAt, string Token = "",
    DateTime? ArgumentSourceStart = null);
// ArgumentSourceStart (tuỳ chọn, thêm 2026-11-16 — Codex DEC-598 v2 P1#2): StartTime của client NGUỒN
// của argument đang lưu = watermark tự cập nhật, LƯU để sống qua lần khởi động lại Booster. File cũ
// không có trường ⇒ null ⇒ PickArgumentUpdate không ghi đè khi còn client chạy bằng argument đang lưu.

/// <summary>Kết quả một lượt LaunchClientsToTotalAsync (thêm các trường cho xác nhận argument khởi động).</summary>
public sealed record LaunchRoundResult(
    int Launched, string Message,
    int StaleArgumentPid = 0, DateTime StaleArgumentStart = default,
    bool NewClientValid = false, DateTime? ValidSourceStart = null, bool RoundStopped = false,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// Kết quả EnsureClientsRunningAsync. ArgumentSourceStart = StartTime của client NGUỒN đã sinh/xác
/// nhận argument trả về (null nếu không biết) — ViewModel dùng làm watermark tự cập nhật (Codex
/// P2#6: không dùng thời điểm ghi).
/// </summary>
public sealed record EnsureResult(int Launched, string Message, string Token, DateTime? ArgumentSourceStart = null);

public class RunningClient
{
    public int    Pid       { get; set; }
    public string StartTime { get; set; } = string.Empty;
    // Hỏi thẳng WinAPI (WindowHelper.IsClientVisible) mỗi lần refresh thay vì
    // tự lưu cờ nội bộ — tránh lệch trạng thái nếu app tắt/mở lại giữa lúc ẩn.
    public bool   IsVisible { get; set; } = true;
    // Thư mục (trong danh sách đã cấu hình) sở hữu tiến trình này, suy ra từ
    // Process.MainModule.FileName. null = không khớp thư mục nào đã biết
    // (bucket "không rõ thư mục" trên UI) — KHÔNG được coi là lỗi, có thể là
    // client mở từ thư mục người dùng chưa thêm/quét ra.
    public string? FolderPath { get; set; }

    /// <summary>Chuỗi hiển thị gộp sẵn cho UI (tránh phải bind numeric field trực tiếp trong x:Bind).</summary>
    public string DisplayText =>
        $"PID {Pid}  ·  {StartTime}" + (IsVisible ? "" : "  ·  (đang ẩn)");
}

public class MultiClientService
{
    /// <summary>
    /// Giãn cách giữa hai lần mở client liên tiếp, tính từ thời điểm gọi
    /// launch (không phải từ lúc xác nhận xong).
    ///
    /// SKILL.md mục 5 ghi mức sàn 3000ms cho việc tránh tranh chấp tài nguyên
    /// / crash launcher DXVK. Nhưng thực nghiệm cho thấy còn một ràng buộc
    /// KHẮT KHE HƠN: mỗi client cần ~10 giây để xác thực xong với server. Mở
    /// client kế tiếp trong lúc client trước còn đang xác thực (cùng một
    /// token) sẽ khiến nó bị "Mạng đứt kết nối" rồi mới tự vào lại sau ~10s.
    /// Quan sát thực tế: mở 4 client cách nhau 3s → client 3 và 4 đều dính.
    ///
    /// Nay đã phát hiện được thời điểm client kết nối xong bằng cách đọc bảng
    /// TCP theo PID (xem WaitForClientConnectedAsync), nên con số cứng này
    /// chỉ còn giữ vai trò SÀN cho ràng buộc DXVK — trả về 3000ms.
    /// </summary>
    private const int MinLaunchIntervalMs = 3000;

    /// R3 Major 2: ngan sach ket thuc den ket thuc cua MOT lan mo qua extension,
    /// tinh tu TRUOC BeginLaunch (khong phai sau khi da thay PID) — cho PID va cho helper khong
    /// duoc cong don thanh hai cua rieng. Phan du phong danh cho buoc don dep sau khi cho.
    private const int ExtTotalBudgetMs = 30000;
    private const int ExtCleanupReserveMs = 2000;

    /// <summary>
    /// Giới hạn thời gian chờ một client kết nối vào server. Hết giờ thì vẫn
    /// mở tiếp thay vì treo — có thể người dùng chưa chọn nhân vật/vào game.
    /// </summary>
    private const int ConnectWaitTimeoutMs = 60000;

    // TokenStaleGraceMs (8000) + WaitForProcessStillAliveAsync đã BỎ (2026-11-16, FCT-2658):
    // "client còn sống sau 8 s" không phân biệt được argument khởi động sai — client mang argument
    // sai treo ~60 s rồi mới chết, nên kiểm đó luôn đạt và Booster nhân bản client hỏng mãi.
    // Nay argument đã lưu được xác nhận bằng tín hiệu thật: client phải KẾT NỐI server
    // (ObserveLaunchedClientAsync — chết/WER treo trong cửa sổ quan sát mới là argument hỏng).

    /// <summary>
    /// Token giờ là PER-FOLDER (mỗi thư mục = 1 tài khoản/phiên riêng — xem
    /// ghi chú tại GameFolderEntry.Token), nên rủi ro "hai thư mục cùng xác
    /// thực chung 1 token" không còn. Vẫn giữ khoá TOÀN BỘ luồng mở-client
    /// (EnsureClientsRunningAsync) qua semaphore tĩnh này vì lý do khác: launch
    /// helper spawn tiến trình con dùng chung tài nguyên (log file DXVK, UAC
    /// prompt) — hai thư mục launch đúng lúc nhau vẫn có thể tranh chấp ở tầng
    /// đó (xem MinLaunchIntervalMs). Không phải để cộng dồn MinLaunchIntervalMs
    /// của nhau — constant đó vẫn chỉ là pacing NỘI BỘ một lần gọi, xem
    /// LaunchClientsToTotalAsync.
    /// </summary>
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

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

        // 2. Nếu không thấy, quét thêm nhưng CHỈ 2 CẤP con — KHÔNG dùng
        // SearchOption.AllDirectories như trước.
        //
        // Quét cạn cả cây thư mục khiến hàm này mất nhiều giây với một thư mục
        // KHÔNG phải thư mục game (vd "D:\Games" lọt vào danh sách qua lượt tự
        // quét): nó phải đi hết hàng trăm nghìn file mới kết luận được "không
        // có". Hàm bị gọi nhiều lần trên cả đường hiển thị lẫn đường launch,
        // nên đó là một nguồn treo app. Bản cài game luôn đặt exe ở gốc hoặc
        // bin64/bin (đã xử ở bước 1), nên 2 cấp là quá đủ; sâu hơn thế thì thà
        // trả null để người dùng chỉ đúng thư mục còn hơn treo cả app.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                var direct = Path.Combine(dir, exeName);
                if (File.Exists(direct)) return direct;

                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var deeper = Path.Combine(sub, exeName);
                        if (File.Exists(deeper)) return deeper;
                    }
                }
                catch { /* nhánh không đọc được — bỏ qua, không chặn cả lượt tìm */ }
            }
        }
        catch { }

        return null;
    }

    // ── Danh sách nhiều thư mục game (game_folders.json) ─────
    private static readonly string GameFoldersFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "game_folders.json");

    /// <summary>
    /// Đọc danh sách thư mục đã lưu. Nếu file chưa từng tồn tại VÀ
    /// aow_token.json cũ còn field GameFolder không rỗng, seed danh sách mới
    /// từ đúng 1 thư mục đó một lần duy nhất (migration) rồi lưu lại — từ đây
    /// về sau GameFolder trong token file không còn được đọc nữa.
    /// </summary>
    public static List<GameFolderEntry> LoadGameFolders()
    {
        var (_, entries, _) = TryLoadGameFoldersCore();
        try
        {
            return entries.Select(f => f with { Token = SecretStore.Unprotect(f.Token) }).ToList();
        }
        catch (Exception ex)
        {
            // Kho mã hoá hỏng/máy khác ⇒ bỏ cả danh sách thay vì ném ra lệnh gọi (giống đường đọc hỏng).
            DiagnosticLogService.Trace($"LoadGameFolders Unprotect EXCEPTION: {ex}");
            return new List<GameFolderEntry>();
        }
    }

    /// <summary>
    /// R5 Major 3: phân biệt "chưa có gì để đọc" (file chưa tồn tại, hoặc vừa migrate) và "đọc hỏng"
    /// (file có nhưng sai định dạng / không đọc được) — chỉ loại sau mới được phép coi file cũ là rác.
    /// </summary>
    public static (bool Ok, List<GameFolderEntry> Entries, Exception? Error) TryLoadGameFolders()
    {
        var (ok, entries, error) = TryLoadGameFoldersCore();
        try
        {
            return (ok, entries.Select(f => f with { Token = SecretStore.Unprotect(f.Token) }).ToList(), error);
        }
        catch (Exception unprotectEx)
        {
            // Kho mã hoá của máy này không giải được token ⇒ coi như lượt đọc hỏng, trả nguyên nhân thật.
            DiagnosticLogService.Trace($"TryLoadGameFolders Unprotect EXCEPTION: {unprotectEx}");
            return (false, new List<GameFolderEntry>(), unprotectEx);
        }
    }

    /// <summary>
    /// #30: đọc THÔ game_folders.json — token còn là chuỗi đã mã hóa. Tách riêng để cả
    /// LoadGameFolders() lẫn TryLoadGameFolders() đều tự gọi SecretStore.Unprotect tại chỗ.
    /// </summary>
    private static (bool Ok, List<GameFolderEntry> Entries, Exception? Error) TryLoadGameFoldersCore()
    {
        try
        {
            // R6 Major 2: KHÔNG dùng File.Exists — nó nuốt lỗi quyền truy cập thành "false" và biến
            // một file hỏng thành "chưa có gì để đọc". Đọc thẳng; chỉ lỗi "không có file" mới là absent.
            string json;
            try
            {
                json = File.ReadAllText(GameFoldersFilePath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Chưa có file mới — thử migrate từ token cũ (chỉ 1 lần).
                var legacy = LoadToken();
                if (!string.IsNullOrWhiteSpace(legacy?.GameFolder) && Directory.Exists(legacy.GameFolder))
                {
                    var seeded = new List<GameFolderEntry>
                    {
                        new(legacy!.GameFolder, 2, DateTimeOffset.Now)
                    };
                    SaveGameFolders(seeded);
                    DiagnosticLogService.Trace(
                        $"Migrate game_folders.json từ aow_token.json cũ: {legacy.GameFolder}");
                    return (true, seeded, null);
                }
                return (true, new List<GameFolderEntry>(), null);
            }

            var loaded = JsonSerializer.Deserialize<List<GameFolderEntry>>(json);
            if (loaded == null || loaded.Any(e => e is null || string.IsNullOrWhiteSpace(e.Path)))
                return (false, new List<GameFolderEntry>(), new InvalidDataException("Tệp game_folders.json sai định dạng."));
            return (true, loaded, null);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"LoadGameFolders EXCEPTION: {ex}");
            return (false, new List<GameFolderEntry>(), ex);
        }
    }

    public static void SaveGameFolders(List<GameFolderEntry> folders)
    {
        try
        {
            var dir = Path.GetDirectoryName(GameFoldersFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(GameFoldersFilePath,
                JsonSerializer.Serialize(
                    folders.Select(f => f with { Token = SecretStore.Protect(f.Token) }).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"SaveGameFolders EXCEPTION: {ex}");
        }
    }

    /// <summary>Dùng cho AowBoosterPage "mượn" thư mục — thay cho GameFolder cũ đã legacy.</summary>
    public static string? GetFirstGameFolderOrNull()
    {
        var list = LoadGameFolders();
        return list.Count > 0 ? list[0].Path : null;
    }

    /// <summary>
    /// Quét mọi ổ đĩa cố định, đào 2-3 cấp thư mục từ gốc mỗi ổ, kiểm tra bằng
    /// ValidateGameFolder (KHÔNG viết lại logic detect fxlaunch/fxgame — dùng
    /// nguyên hàm đã kiểm chứng). Chạy có thể mất tới ~1 phút trên máy nhiều
    /// ổ — gọi hàm này từ Task.Run ở tầng ViewModel, KHÔNG gọi trực tiếp trên
    /// UI thread. Gặp thư mục hợp lệ thì dừng đào sâu tiếp nhánh đó (không cần
    /// tìm bản cài đặt lồng bên trong bản cài đặt khác).
    /// </summary>
    public static List<string> DiscoverGameFolders(int maxDepth = 3, IProgress<string>? progress = null)
    {
        var found = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToArray();
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"DiscoverGameFolders — không liệt kê được ổ đĩa: {ex.Message}");
            return found;
        }

        foreach (var drive in drives)
        {
            progress?.Report($"Đang quét ổ {drive.Name} ...");
            try
            {
                ScanDirectoryForGame(drive.RootDirectory.FullName, depth: 0, maxDepth, found);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Trace($"DiscoverGameFolders — lỗi quét {drive.Name}: {ex.Message}");
            }
        }

        DiagnosticLogService.Trace($"DiscoverGameFolders — tìm thấy {found.Count} thư mục hợp lệ");
        return found;
    }

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System Volume Information", "ProgramData", "$Recycle.Bin", "Recovery"
    };

    private static void ScanDirectoryForGame(string dir, int depth, int maxDepth, List<string> found)
    {
        try
        {
            var (valid, _) = ValidateGameFolder(dir);
            if (valid)
            {
                found.Add(dir);
                return; // đã ra kết quả ở nhánh này, không cần đào sâu thêm
            }
        }
        catch { /* thư mục không đọc được (quyền truy cập...) — bỏ qua, không chặn scan */ }

        if (depth >= maxDepth) return;

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return; // không có quyền đọc thư mục này — bỏ qua nhánh
        }

        foreach (var sub in subDirs)
        {
            try
            {
                var name = Path.GetFileName(sub);
                if (!string.IsNullOrEmpty(name) && SkipDirNames.Contains(name)) continue;
                ScanDirectoryForGame(sub, depth + 1, maxDepth, found);
            }
            catch { /* một nhánh lỗi không được làm hỏng cả lượt quét */ }
        }
    }

    // ResolveOwningFolder (so TIỀN TỐ, lấy thư mục khớp đầu tiên) đã BỎ 2026-11-16 (Codex DEC-598 v3
    // P1#1): thêm D:\AOW_Dev trước D:\AOW_Dev\Test thì client của Test bị gán cho dòng cha và "Tắt tất
    // cả" dòng cha giết luôn nó (cả cây tiến trình). Mọi chỗ gán/đếm/liệt kê/tắt client theo thư mục giờ
    // dùng chung LaunchArgumentLogic.IsGameExeOfFolder / ResolveOwningFolder (khớp ĐÚNG đường dẫn exe).

    // ── Bước 1: Mở client đầu tiên qua fxlaunch ──────────
    public static async Task<(bool Success, string Message)> LaunchFirstClientAsync(string gameFolder)
    {
        try
        {
            var launcher = FindGameExe(gameFolder, "fxlaunch.exe");
            if (launcher == null)
                return (false, "Không tìm thấy fxlaunch.exe trong thư mục đã chọn.");

            var workDir = Path.GetDirectoryName(launcher);
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
                workDir = gameFolder;

            var psi = new ProcessStartInfo
            {
                FileName         = launcher,
                WorkingDirectory = workDir,
                UseShellExecute  = true
            };

            using var proc = Process.Start(psi);
            await Task.Delay(100); // Give it a tiny bit of time
            return (true, "Đã gọi fxlaunch.exe. Chờ vài giây để game khởi động...");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Thao tác khởi chạy đã bị hủy (UAC cancellation).");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi: {ex.Message}");
        }
    }

    // ── Bước 2: Lấy argument khởi động từ fxgame.exe của ĐÚNG thư mục ────────
    /// <summary>
    /// Đọc argument khởi động (trong code cũ gọi là "token") từ dòng lệnh của một fxgame.exe
    /// THUỘC <paramref name="gameFolder"/>. PID mới (không có trong <paramref name="pidsBefore"/>)
    /// được thử trước; <paramref name="newOnly"/> = true thì chỉ PID mới.
    ///
    /// Trước 2026-11-16 hàm này đọc fxgame.exe BẤT KỲ và lấy cái đầu tiên có argument — đang có
    /// client của thư mục khác hoặc client cũ mang argument hỏng là bắt nhầm (FCT-2658: thêm lại
    /// D:\AOW_Dev thì lưu argument sai, mọi client sau đó treo ~60 s rồi chết). Nay PID không đọc
    /// được đường dẫn exe thì không bao giờ được chọn: không biết thuộc thư mục nào thì không lấy.
    /// </summary>
    public static async Task<(bool Success, string Token, string Message, int Pid)> DetectTokenAsync(
        string gameFolder, IReadOnlyCollection<int>? pidsBefore, bool newOnly)
    {
        // #8: đường CHÍNH là đọc CommandLine trực tiếp từ PEB (ProcessCommandLine) — không qua
        // System.Management, tức không qua DỊCH VỤ WMI. WMI trên máy người dùng từng treo "hàng
        // chục phút" và được đo là ném 11 exception / 224 phiên (mỗi lần hỏng ~5s). WMI vẫn ở lại,
        // nhưng chỉ là FALLBACK cho ca duy nhất mà PEB bó tay: tiến trình đích ở mức quyền khác
        // (OpenProcess bị chặn). Đã đo: đọc 32-bit lẫn 64-bit bằng một đường, và PID không mở được
        // trả false trong 0 ms (experiments/process-command-line).
        return await Task.Run(() =>
        {
            int[] pids;
            try
            {
                // Lọc theo đường dẫn exe (QueryFullProcessImageName, không WMI) TRƯỚC khi đọc dòng lệnh.
                var paths = GetFxgameExecutablePaths();
                pids = LaunchArgumentLogic.OrderCandidatePids(
                    paths, gameFolder, pidsBefore, newOnly, GetFxgameStartTimes(paths.Keys),
                    FindGameExe(gameFolder, "fxgame.exe")).ToArray();
            }
            catch { pids = Array.Empty<int>(); }

            int unreadable = 0;
            foreach (var pid in pids)
            {
                if (!ProcessCommandLine.TryGet(pid, out string cmdLine)) { unreadable++; continue; }
                string token = ParseTokenFromCommandLine(cmdLine);
                if (!string.IsNullOrEmpty(token))
                    return (true, token, "Đã lấy được argument khởi động.", pid);
            }

            // Chưa có client nào của thư mục này → không có gì để đọc, khỏi cần WMI.
            if (pids.Length == 0)
                return (false, string.Empty,
                    newOnly
                        ? "Chưa thấy fxgame.exe mới của thư mục này (đang chờ đăng nhập qua launcher)."
                        : "Không thấy fxgame.exe nào của thư mục này đang chạy.", 0);

            // Có fxgame nhưng không đọc được CommandLine (khác mức quyền) → mới cần tới WMI.
            if (unreadable == pids.Length)
                return DetectTokenViaWmi(pids);

            return (false, string.Empty,
                $"Thấy {pids.Length} fxgame của thư mục này nhưng chưa thấy argument khởi động trong dòng lệnh — client có thể chưa nhận xong tham số.", 0);
        });
    }

    // Fallback CÓ GIỚI HẠN (5s) cho ca PEB không mở được tiến trình đích. Chỉ nhận PID nằm trong
    // danh sách ứng viên đã lọc theo thư mục (theo đúng thứ tự ưu tiên PID mới) — WMI trả về mọi
    // fxgame.exe trên máy, nên không lọc ở đây là quay lại đúng lỗi bắt nhầm thư mục.
    private static (bool Success, string Token, string Message, int Pid) DetectTokenViaWmi(IReadOnlyList<int> candidatePids)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope("root\\cimv2"),
                new ObjectQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'fxgame.exe'"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) });
            using var collection = searcher.Get();

            var byPid = new Dictionary<int, string>();
            foreach (ManagementObject obj in collection)
            {
                using (obj)
                {
                    try
                    {
                        int pid = Convert.ToInt32(obj["ProcessId"]);
                        var cmdLine = obj["CommandLine"]?.ToString();
                        if (!string.IsNullOrEmpty(cmdLine)) byPid[pid] = cmdLine;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            foreach (var pid in candidatePids)
            {
                if (!byPid.TryGetValue(pid, out var cmdLine)) continue;
                string token = ParseTokenFromCommandLine(cmdLine);
                if (!string.IsNullOrEmpty(token))
                    return (true, token, "Đã lấy được argument khởi động (WMI fallback).", pid);
            }

            return (false, string.Empty,
                $"Thấy {candidatePids.Count} fxgame của thư mục này nhưng WMI cũng không đọc được dòng lệnh (client chưa nhận tham số?).", 0);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"Lỗi WMI: {ex.Message}", 0);
        }
    }

    // Chuỗi dạng: "C:\Path\fxgame.exe" ARG hoặc C:\Path\fxgame.exe ARG — logic thuần nằm ở
    // LaunchArgumentLogic để lab experiments/launch-argument kiểm được chính mã này.
    private static string ParseTokenFromCommandLine(string cmdLine) => LaunchArgumentLogic.ParseArgument(cmdLine);

    /// <summary>Thời điểm khởi động của các PID (bỏ qua PID không đọc được) — dùng để xếp PID mới nhất trước.</summary>
    private static Dictionary<int, DateTime> GetFxgameStartTimes(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, DateTime>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                result[pid] = p.StartTime;
            }
            catch { /* đã thoát / khác quyền — xếp sau cùng, không chặn */ }
        }
        return result;
    }

    /// <summary>
    /// E: mỗi lần làm mới/đổi argument khởi động chỉ ghi thư mục, PID nguồn, độ dài và
    /// sha256[:8] — tuyệt đối không ghi giá trị (argument là phiên đăng nhập của người chơi).
    /// </summary>
    private static void LogArgumentChange(string reason, string gameFolder, int sourcePid, string oldArg, string newArg)
    {
        DiagnosticLogService.Trace(
            $"[argument khởi động] {reason} — thư mục={gameFolder}, PID nguồn={sourcePid}, " +
            $"mới: {LaunchArgumentLogic.Describe(newArg)}, cũ: {LaunchArgumentLogic.Describe(oldArg)}");
    }

    /// <summary>
    /// D: tìm argument khởi động mới từ fxgame.exe ỔN ĐỊNH của thư mục này (vd người dùng tự mở qua
    /// launcher): sống ≥ <see cref="LaunchArgumentLogic.StableClientAge"/>, không bị WER treo, bất kể
    /// đi qua kiểu kết nối nào (gearup/proxy LAN cũng được — Codex P1#5). Chỉ nhận client khởi động
    /// SAU <paramref name="watermark"/> = StartTime của client nguồn của argument đang lưu (Codex
    /// P2#6). Trả null nếu không có gì để cập nhật. Chỉ đọc PEB (không WMI): hàm chạy định kỳ ở nền,
    /// WMI có thể treo hàng chục phút. Không ghi log khi không đổi gì.
    /// </summary>
    public static FolderClientSnapshot? FindUpdatedArgument(string gameFolder, string savedArgument, DateTime? watermark)
    {
        try
        {
            var paths = GetFxgameExecutablePaths();
            var pids = LaunchArgumentLogic.OrderCandidatePids(paths, gameFolder, null, newOnly: false,
                findGameExePath: FindGameExe(gameFolder, "fxgame.exe"));
            if (pids.Count == 0) return null;
            var starts = GetFxgameStartTimes(pids);
            var now = DateTime.Now;

            // MỌI client đọc được argument đều vào danh sách (kể cả trẻ / WER treo): khi watermark =
            // null, chỉ cần còn một client chạy bằng argument đang lưu là không được ghi đè (v2 P1#2).
            // Lọc tuổi/WER/watermark nằm trong PickArgumentUpdate (thuần, có lab).
            var snapshots = new List<FolderClientSnapshot>();
            foreach (var pid in pids)
            {
                if (!starts.TryGetValue(pid, out var start)) continue;
                if (!ProcessCommandLine.TryGet(pid, out string cmdLine)) continue;
                var arg = ParseTokenFromCommandLine(cmdLine);
                if (string.IsNullOrEmpty(arg)) continue;
                // Chỉ lấy mẫu thread (tốn hơn) khi client đủ tuổi; mẫu không đọc được (null) ⇒ coi như
                // WER-treo: không chắc thì không nhận.
                bool wer = now - start < LaunchArgumentLogic.StableClientAge || SampleWerSuspended(pid) != false;
                snapshots.Add(new FolderClientSnapshot(pid, start, arg, wer));
            }

            var pick = LaunchArgumentLogic.PickArgumentUpdate(
                snapshots, savedArgument, watermark, now, LaunchArgumentLogic.StableClientAge);
            if (pick != null)
                LogArgumentChange(
                    $"tự cập nhật từ client ổn định (sống ≥ {LaunchArgumentLogic.StableClientAge.TotalMinutes:0} phút, không bị WER treo)",
                    gameFolder, pick.Pid, savedArgument, pick.Argument);
            return pick;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"[argument khởi động] FindUpdatedArgument lỗi: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Tắt ĐÚNG client mang argument hỏng: PID đó phải còn là fxgame.exe của thư mục game này VÀ
    /// còn đúng StartTime lúc được chọn (PID có thể đã bị tái sử dụng). Không bao giờ đụng client khác.
    /// </summary>
    private static void KillStaleClient(int pid, DateTime expectedStart, string gameFolder, string reason)
    {
        if (pid <= 0) return;
        bool sameProcess = GetFxgameExecutablePaths().TryGetValue(pid, out var exe) &&
                           LaunchArgumentLogic.IsGameExeOfFolder(exe, gameFolder, FindGameExe(gameFolder, "fxgame.exe")) &&
                           GetFxgameStartTimes(new[] { pid }).TryGetValue(pid, out var start) &&
                           (expectedStart == DateTime.MinValue || start == expectedStart);
        if (!sameProcess)
        {
            DiagnosticLogService.Trace(
                $"[argument khởi động] KHÔNG tắt PID={pid}: không còn là fxgame.exe của lần mở này ({gameFolder}) hoặc đã thoát");
            return;
        }
        bool ok = KillClient(pid);
        DiagnosticLogService.Trace(
            $"[argument khởi động] tắt PID={pid} ({gameFolder}) — lý do: {reason} — {(ok ? "đã tắt" : "tắt thất bại")}");
    }

    /// <summary>
    /// Một mẫu "WER đang xử lý crash" (gần như mọi thread Wait/Suspended — FCT-2656/2657). null = không
    /// đọc được (đã thoát / khác quyền). Đọc qua Process.Threads (NtQuerySystemInformation, không mở
    /// handle thread, không WMI).
    /// </summary>
    private static bool? SampleWerSuspended(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var threads = p.Threads;
            int total = threads.Count, suspended = 0;
            foreach (ProcessThread t in threads)
            {
                try
                {
                    if (t.ThreadState == System.Diagnostics.ThreadState.Wait &&
                        t.WaitReason == ThreadWaitReason.Suspended)
                        suspended++;
                }
                catch { /* thread vừa thoát giữa chừng */ }
                finally { try { t.Dispose(); } catch { } }
            }
            return LaunchArgumentLogic.IsWerSuspendedSample(total, suspended);
        }
        catch { return null; }
    }

    /// <summary>
    /// A + P1#1: chờ PID của ĐÚNG lần mở vừa rồi — fxgame.exe MỚI (không có trong
    /// <paramref name="pidsBefore"/>), exe thuộc <paramref name="gameFolder"/>, và dòng lệnh mang đúng
    /// <paramref name="expectedArgument"/> (PID đọc được dòng lệnh mà argument KHÁC thì không bao giờ
    /// được chọn — đó là lần mở của thư mục/người khác). Trả (0, MinValue) nếu hết giờ.
    /// </summary>
    private static async Task<(int Pid, DateTime Start)> WaitForLaunchedFolderPidAsync(
        string gameFolder, HashSet<int> pidsBefore, string expectedArgument, int timeoutMs)
    {
        const int pollInterval = 500;

        // R5 Major 1: han <= 0 ⇒ khong bao gio poll (truoc day van vao 1 luot WMI/process/command-line).
        if (timeoutMs <= 0) return (0, DateTime.MinValue);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int step = (int)Math.Min(pollInterval, Math.Max(0, timeoutMs - sw.ElapsedMilliseconds));
            if (step > 0) await Task.Delay(step);
            // R5 Major 1: kiem tra lai han SAU khi cho — delay da an het timeoutMs thi khong duoc bat
            // dau mot luot WMI/process/command-line nua (han 30 giay cua luot phai giu dung).
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            try
            {
                var paths = GetFxgameExecutablePaths();
                var starts = GetFxgameStartTimes(paths.Keys);
                // R6 Major 1: truy van WMI vua roi da co the an het han — kiem tra lai truoc khi chon PID.
                if (sw.ElapsedMilliseconds >= timeoutMs) break;
                var fresh = LaunchArgumentLogic.OrderCandidatePids(paths, gameFolder, pidsBefore, newOnly: true, starts,
                    FindGameExe(gameFolder, "fxgame.exe"));
                if (fresh.Count == 0) continue;

                var args = new Dictionary<int, string?>();
                foreach (var pid in fresh)
                {
                    if (sw.ElapsedMilliseconds >= timeoutMs) break;
                    args[pid] = ProcessCommandLine.TryGet(pid, out var cmd) ? ParseTokenFromCommandLine(cmd) : null;
                }
                if (sw.ElapsedMilliseconds >= timeoutMs) break;

                int chosen = LaunchArgumentLogic.SelectLaunchedPid(fresh, args, expectedArgument);
                if (chosen != 0)
                {
                    DiagnosticLogService.Trace(
                        $"  client mới PID={chosen} của {gameFolder} xuất hiện sau {sw.ElapsedMilliseconds}ms" +
                        (args[chosen] == null ? " (không đọc được dòng lệnh — chọn theo thư mục + PID mới)" : ""));
                    return (chosen, starts.TryGetValue(chosen, out var s) ? s : DateTime.MinValue);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Trace($"  WaitForLaunchedFolderPid EXCEPTION: {ex.GetType().Name}");
            }
        }

        DiagnosticLogService.Trace($"  TIMEOUT {timeoutMs}ms — chưa thấy client mới của {gameFolder} mang đúng argument vừa mở");
        return (0, DateTime.MinValue);
    }

    /// <summary>
    /// B/C: xác nhận argument khởi động bằng tín hiệu THẬT (định hướng kiến trúc sư sau review Codex
    /// vòng 1, dựa trên đo FCT-2656/2657/2658): quan sát client vừa mở trọn
    /// <see cref="LaunchArgumentLogic.ObserveWindowMs"/>. Stale CHỈ khi nó CHẾT hoặc bị WER treo kéo
    /// dài; Valid khi sống trọn cửa sổ VÀ đã có kết nối công; còn lại Unknown (proxy LAN/cục bộ,
    /// mạng chậm) ⇒ caller không tắt, không làm mới, chỉ log.
    /// </summary>
    private static async Task<ArgumentVerdict> ObserveLaunchedClientAsync(
        int pid, string waitingMessagePrefix, IProgress<string>? progress)
    {
        const int pollInterval = 2000;
        var obs = new LaunchObserver();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? remote = null;

        while (sw.ElapsedMilliseconds < LaunchArgumentLogic.ObserveWindowMs)
        {
            bool alive = IsProcessAlive(pid);
            bool pub = alive && TcpTableHelper.HasEstablishedPublicConnection(pid);
            if (pub && remote == null)
            {
                remote = TcpTableHelper.GetFirstRemoteAddress(pid);
                DiagnosticLogService.Trace(
                    $"  PID={pid} đã kết nối server ({remote}) sau {sw.ElapsedMilliseconds}ms — tiếp tục quan sát cho đủ cửa sổ");
            }
            obs.Sample(alive, alive ? SampleWerSuspended(pid) : null, pub);
            if (obs.ShouldStopEarly) break;

            progress?.Report($"{waitingMessagePrefix} ({sw.ElapsedMilliseconds / 1000}/{LaunchArgumentLogic.ObserveWindowMs / 1000}s)...");
            await Task.Delay(pollInterval);
        }

        bool aliveEnd = IsProcessAlive(pid);
        var o = obs.Finish(aliveEnd, aliveEnd ? SampleWerSuspended(pid) : null);
        var verdict = LaunchArgumentLogic.ClassifyObservation(o);
        DiagnosticLogService.Trace(
            $"  PID={pid} phán quyết argument khởi động sau {sw.ElapsedMilliseconds}ms: {verdict} " +
            $"(chết={o.Died}, WER treo={o.SustainedWerSuspend}, từng có kết nối công={o.SawPublicConnection}, sống trọn cửa sổ={o.AliveThroughWindow})" +
            (o.SawPublicConnection || o.Died ? "" :
                $" — {TunnelProbe.Current()} · TCP: {TcpTableHelper.DescribeConnections(pid)} · {TcpTableHelper.DescribeForwarderChain(pid)}"));
        return verdict;
    }

    // ── Đọc token cũ (legacy, chỉ dùng cho migration) ────────
    // Token KHÔNG còn ghi ở tầng global từ khi chuyển sang per-folder — file
    // aow_token.json chỉ còn được ĐỌC (không ghi) để migrate field GameFolder
    // cũ một lần duy nhất (xem LoadGameFolders). Token của thư mục nào giờ
    // sống trong GameFolderEntry.Token của chính thư mục đó.
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

    /// <summary>
    /// Mở game cho tới khi đạt ĐỦ TỔNG số cửa sổ mong muốn (targetTotal),
    /// tính cả các client đang chạy sẵn — người dùng nghĩ theo "tôi muốn chơi
    /// N acc", không phải "mở thêm N cái nữa".
    /// </summary>
    /// <param name="progress">
    /// Nhận thông báo tiến độ để hiển thị lên UI. Giãn cách giữa các client
    /// khá dài (xem MinLaunchIntervalMs) nên bắt buộc phải cho người dùng
    /// thấy app đang chờ chứ không phải treo.
    /// </param>
    /// <param name="newClientValidThisClick">
    /// true CHỈ khi một client MỚI của CÙNG lần bấm (client nguồn từ launcher) đã được quan sát ra
    /// Valid. Khi false, client MỚI ĐẦU TIÊN của lượt — kể cả khi nó cũng là client cuối (Codex v1
    /// P1#4) — LUÔN bị quan sát trọn cửa sổ; client phiên cũ đang chạy KHÔNG BAO GIỜ thay được bước
    /// này (Codex v2 P1#4). Chết/WER treo ⇒ dừng lượt (StaleArgumentPid) thay vì nhân bản client hỏng.
    /// Client tiếp theo trong lượt chỉ được bỏ quan sát sau khi một client mới đã ra Valid.
    /// </param>
    public static async Task<LaunchRoundResult> LaunchClientsToTotalAsync(
        string gameFolder, string token, int targetTotal, IProgress<string>? progress = null, bool useLocalExtension = false,
        bool newClientValidThisClick = false, bool useSecondaryLocalExtension = false)
    {
        if (LocalLaunchExtension.Current is { } preExt)
        {
            await preExt.BeforeFolderLaunchAsync(gameFolder, progress);
        }

        var gamePath = FindGameExe(gameFolder, "fxgame.exe");
        if (gamePath == null)
            return new LaunchRoundResult(0, "Không tìm thấy fxgame.exe.");

        // Đếm THEO THƯ MỤC (không phải toàn hệ thống) — hỗ trợ nhiều thư mục
        // chạy song song mà không cộng dồn số cửa sổ của nhau.
        int alreadyRunning = CountRunningFxgameInFolder(gameFolder);
        int count = targetTotal - alreadyRunning;

        DiagnosticLogService.Trace(
            $"LaunchClientsToTotal — mục tiêu {targetTotal}, đang chạy {alreadyRunning} → cần mở thêm {count}");

        if (count <= 0)
            return new LaunchRoundResult(0, $"Đã có đủ {alreadyRunning}/{targetTotal} cửa sổ game đang chạy.");

        int launched = 0;
        var errors   = new List<string>();
        int lastNewPid = 0;   // PID cua client CUOI cung — thứ không bao giờ được chờ (xem batch 66)
        bool newClientValid = newClientValidThisClick;
        DateTime? validStart = null;
        int staleArgumentPid = 0;
        DateTime staleArgumentStart = DateTime.MinValue;
        bool roundStopped = false;

        for (int i = 0; i < count; i++)
        {
            try
            {
                var pidsBefore = GetFxgamePids();
                DiagnosticLogService.Trace($"[client {i + 1}/{count}] trước khi start: {pidsBefore.Count} fxgame đang chạy | {TunnelProbe.Current()}");

                var launchedAt = System.Diagnostics.Stopwatch.StartNew();

                // Mở qua helper, KHÔNG gọi Process.Start(fxgame) trực tiếp:
                // fxgame.exe giết tiến trình cha của nó (cơ chế tự đóng
                // launcher của game) — nếu gọi thẳng thì app này bị giết.
                progress?.Report($"Đang mở cửa sổ {alreadyRunning + i + 1}/{targetTotal}...");
                ILocalLaunchSession? extSession = null;
                int newPid;
                DateTime newStart;
                try
                {
                    // R3 Major 2: MOT dong ho tuyet doi chay tu TRUOC BeginLaunch — khong phai tu luc
                    // xong buoc cho PID. Budget duoc tinh bang thoi gian con lai, khong cong don hai cua.
                    var extStopwatch = Stopwatch.StartNew();
                    if ((useLocalExtension || useSecondaryLocalExtension) && LocalLaunchExtension.Current is { } ext)
                        extSession = ext.BeginLaunch(gameFolder, gamePath, token, useLocalExtension, useSecondaryLocalExtension);
                    else
                        LauncherHelper.LaunchGameViaHelper(gamePath, token);
                    DiagnosticLogService.Trace($"[client {i + 1}/{count}] đã gọi helper, chờ tiến trình xuất hiện...");

                    // Chỉ PID mới của ĐÚNG thư mục, mang ĐÚNG argument vừa truyền (Codex P1#1): một client
                    // của thư mục khác xuất hiện cùng lúc không được thay chỗ client của lần mở này.
                    // Major 2: MOT han chung 30 giay cho ca luot (chờ PID + chờ helper), tinh tu lan goi
                    // BeginLaunch — khong cong don hai cua so rieng thanh 40 giay mot client.
                    // R4 Major 1: buoc chờ PID cũng nằm TRONG hạn này (trước đây luôn 15 giây; du
                    // dong ho da chay gan het budget thi tong van vuot 30 giây).
                    int pidTimeoutMs = extSession != null
                        ? Math.Min(15000, Math.Max(0, ExtTotalBudgetMs - ExtCleanupReserveMs - (int)extStopwatch.ElapsedMilliseconds))
                        : 15000;
                    // R5 Major 1: khong con thoi gian cho PID (du phong don dep da het) thi BO QUA luon —
                    // khong duoc poll mot lan de sam nghiem/dong lenh, tranh vet qua han 30 giay.
                    (newPid, newStart) = pidTimeoutMs > 0
                        ? await WaitForLaunchedFolderPidAsync(gameFolder, pidsBefore, token, timeoutMs: pidTimeoutMs)
                        : (0, DateTime.MinValue);
                    // Cung mot han cho ca nhanh PID-found lan nhanh khong thay PID: budget da tru thoi
                    // gian cho PID va phan du phong don dep (khong con san 5 giay keo dai ngan sach).
                    int extRemainingMs = Math.Max(0, ExtTotalBudgetMs - ExtCleanupReserveMs - (int)extStopwatch.ElapsedMilliseconds);
                    // Chỉ TÍNH là đã mở khi thấy PID của chính lần mở này (Codex v3 P1#2).
                    if (extSession != null && LaunchArgumentLogic.CountsAsLaunched(newPid))
                    {
                        var (extOk, extFail) = await extSession.WaitCompletedAsync(timeoutMs: extRemainingMs);
                        if (!extOk)
                        {
                            errors.Add($"Client {i + 1}: {(string.IsNullOrWhiteSpace(extFail) ? await extSession.DescribeFailureAsync() : extFail)}");
                            newPid = 0;
                        }
                        else
                        {
                            launched++;
                            lastNewPid = newPid;
                        }
                    }
                    else if (LaunchArgumentLogic.CountsAsLaunched(newPid))
                    {
                        launched++;
                        lastNewPid = newPid;
                    }
                    else if (extSession != null)
                    {
                        // Khong thay PID: van phai cho helper het CUNG han tren — neu no ghi result.json
                        // sau khi het han nay thi loi do moi la loi dung, khong bao "khong thay tien trinh".
                        var (extOk, extFail) = await extSession.WaitCompletedAsync(timeoutMs: extRemainingMs);
                        errors.Add($"Client {i + 1}: {(extOk ? await extSession.DescribeFailureAsync() : (string.IsNullOrWhiteSpace(extFail) ? await extSession.DescribeFailureAsync() : extFail))}");
                    }
                    else
                    {
                        errors.Add($"Client {i + 1}: không thấy tiến trình game của lần mở này trong 15 giây " +
                                   "(chết ngay khi mở hoặc xuất hiện quá muộn)");
                    }
                }
                finally { extSession?.Dispose(); }

                if (!LaunchArgumentLogic.CountsAsLaunched(newPid) &&
                    LaunchArgumentLogic.ShouldStopOnMissingPid(newClientValid))
                {
                    // Argument CHƯA được xác nhận trong lần bấm này mà không bắt được client của lần mở ⇒
                    // không xác nhận được ⇒ dừng, không nhân bản tiếp bằng argument chưa kiểm (v3 P1#2).
                    roundStopped = true;
                    DiagnosticLogService.Trace(
                        $"[client {i + 1}/{count}] DỪNG mở thêm: không thấy PID của lần mở này và argument khởi động chưa được " +
                        $"xác nhận trong lượt ({LaunchArgumentLogic.Describe(token)})");
                    break;
                }

                bool observed = false;
                if (newPid > 0 && LaunchArgumentLogic.ShouldVerifyClient(newClientValid))
                {
                    // C: quan sát trọn cửa sổ BẤT KỂ vị trí trong lượt (vòng 1 chỉ chờ khi i < count-1).
                    observed = true;
                    var verdict = await ObserveLaunchedClientAsync(
                        newPid,
                        $"Đã mở {alreadyRunning + launched}/{targetTotal} cửa sổ — đang xác nhận argument khởi động",
                        progress);
                    if (verdict == ArgumentVerdict.Valid)
                    {
                        newClientValid = true;
                        if (newStart != DateTime.MinValue) validStart = newStart;
                    }
                    else if (verdict == ArgumentVerdict.Unknown)
                    {
                        // Unknown KHÔNG cho bỏ quan sát client kế tiếp — client sau vẫn bị quan sát.
                        DiagnosticLogService.Trace(
                            $"[client {i + 1}/{count}] PID={newPid} còn sống nhưng không chứng minh được argument " +
                            "(proxy LAN/cục bộ hoặc mạng chậm) — không tắt, không làm mới, mở tiếp và quan sát client sau");
                    }
                    else if (LaunchArgumentLogic.ShouldStopLaunching(verdict, newClientValid))
                    {
                        roundStopped = true;
                        staleArgumentPid = newPid;
                        staleArgumentStart = newStart;
                        DiagnosticLogService.Trace(
                            $"[client {i + 1}/{count}] DỪNG mở thêm: PID={newPid} đã thoát/bị WER treo trong cửa sổ quan sát " +
                            $"và argument chưa được xác nhận trong lượt này — argument khởi động có vẻ đã hết hạn " +
                            $"({LaunchArgumentLogic.Describe(token)})");
                        break;
                    }
                }

                if (i < count - 1)
                {
                    // Chờ client vừa mở KẾT NỐI XONG vào server rồi mới mở cái
                    // tiếp theo. Hai client cùng xác thực một token gần như
                    // đồng thời sẽ khiến một cái bị "Mạng đứt kết nối".
                    // (Client vừa được quan sát trọn cửa sổ thì đã qua bước này.)
                    if (newPid > 0 && !observed)
                    {
                        await WaitForClientConnectedAsync(
                            newPid, alreadyRunning + launched, targetTotal, progress);
                    }

                    // Vẫn giữ sàn tối thiểu của SKILL.md mục 5: launcher DXVK
                    // ghi log vào một file chung, mở quá sát nhau gây tranh
                    // chấp file lock và crash launcher.
                    var remaining = MinLaunchIntervalMs - (int)launchedAt.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        DiagnosticLogService.Trace($"[client {i + 1}/{count}] giữ sàn giãn cách thêm {remaining}ms");
                        await Task.Delay(remaining);
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                DiagnosticLogService.Trace($"[client {i + 1}/{count}] UAC bị hủy");
                errors.Add($"Client {i + 1}: Thao tác đã bị hủy (UAC cancellation).");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Trace($"[client {i + 1}/{count}] EXCEPTION: {ex}");
                errors.Add($"Client {i + 1}: {ex.Message}");
            }
        }

        // Lời báo dựa trên số client THỰC TẾ của thư mục đếm lại sau lượt, không dựa vào số lần gọi helper
        // (v3 P1#2: không bao giờ báo thành công khi số client thực tế chưa đạt).
        int actualRunning = CountRunningFxgameInFolder(gameFolder);
        string errText = errors.Count > 0 ? " Lỗi: " + string.Join(", ", errors) : "";
        string msg = staleArgumentPid > 0
            // R3 Major 1: nhanh nay cung phai mang loi da tich luy (helper xac nhan that bai, client
            // khac loi) — khong duoc che chung chi vi da chon duoc cau ve argument het han.
            ? $"Đã dừng ở {actualRunning}/{targetTotal} cửa sổ: client vừa mở đã thoát hoặc bị treo do lỗi trong " +
              $"{LaunchArgumentLogic.ObserveWindowMs / 1000} giây — argument khởi động có vẻ đã hết hạn, cần đăng nhập lại qua launcher (fxlaunch.exe)." + errText
            : roundStopped
            ? $"Đã dừng ở {actualRunning}/{targetTotal} cửa sổ: không thấy tiến trình game của lần mở vừa rồi và argument khởi động " +
              "chưa được xác nhận — không mở thêm để tránh nhân bản client hỏng. Thử lại, hoặc đăng nhập lại qua launcher (fxlaunch.exe)." + errText
            // Major 1: loi tu client bat ky (ke ca helper xac nhan that bai) khong duoc che bang so luong process.
            : errors.Count == 0 && LaunchArgumentLogic.IsRoundSuccess(actualRunning, targetTotal, roundStopped)
            ? $"Đã mở đủ {actualRunning}/{targetTotal} cửa sổ game!"
            : $"Mới có {actualRunning}/{targetTotal} cửa sổ đang chạy (đã mở thêm {launched}).{errText}";

        // Batch 66 (#48): client CUỐI không bao giờ được chờ (vòng đợi nằm trong `if (i < count - 1)`),
        // và đo trên 550 cú mở cho thấy 110/115 cú "+1" không đời nào nhắc lại PID đó nữa
        // (experiments/game-path-probe/analyze_last_client.py) ⇒ lời báo "Đã mở đủ" cho client cuối
        // không có MỘT dòng bằng chứng nào phía sau. Probe trễ này chạy nền, SAU khi đã trả lời người
        // dùng, chỉ ghi log: không đổi hành vi, không chạm UI thread, không thêm lần chờ nào.
        if (lastNewPid > 0)
        {
            int probePid = lastNewPid;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(10000);
                    DiagnosticLogService.Trace(
                        $"[client cuối] PID={probePid} sau 10s (chưa từng được chờ): {TunnelProbe.Current()} | {TcpTableHelper.DescribeConnections(probePid)}");
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Trace($"[client cuối] PID={probePid} không kiểm tra được: {ex.GetType().Name}");
                }
            });
        }

        DiagnosticLogService.Trace(
            $"LaunchClientsToTotal KẾT THÚC — thực tế {actualRunning}/{targetTotal} (mở thêm {launched}, dừng={roundStopped})");
        return new LaunchRoundResult(launched, msg, staleArgumentPid, staleArgumentStart, newClientValid, validStart, roundStopped,
            errors.Count > 0 ? errors.AsReadOnly() : null);
    }

    /// <summary>
    /// Luồng đầy đủ để mở đủ N cửa sổ cho MỘT thư mục — dùng cho UI dạng
    /// nhiều-thư-mục (mỗi hàng gọi hàm này với thư mục + token + đích của
    /// riêng nó — token PER-FOLDER, xem GameFolderEntry.Token). Nếu thư mục
    /// chưa có client nào chạy VÀ chưa có token riêng, tự mở fxlaunch.exe của
    /// CHÍNH thư mục đó + chờ người dùng đăng nhập, đọc token từ fxgame.exe
    /// vừa chạy lên, rồi mới mở nốt cho đủ targetTotal. <paramref name="existingToken"/>
    /// là token đã lưu của RIÊNG thư mục này (rỗng nếu thư mục chưa từng đăng
    /// nhập) — kết quả trả về (Token) là token cuối cùng caller cần lưu lại
    /// vào đúng thư mục đó. Toàn bộ vẫn khoá bằng <see cref="LaunchGate"/> để
    /// hai thư mục launch đồng thời không tranh chấp tài nguyên launch helper
    /// dùng chung — xem ghi chú tại LaunchGate — KHÔNG cộng dồn
    /// <see cref="MinLaunchIntervalMs"/> của nhau, mỗi lời gọi vẫn chỉ trả giá
    /// pacing của chính nó.
    /// </summary>
    public static async Task<EnsureResult> EnsureClientsRunningAsync(
        string gameFolder, string existingToken, int targetTotal, IProgress<string>? progress = null, bool useLocalExtension = false,
        bool useSecondaryLocalExtension = false)
    {
        await LaunchGate.WaitAsync();
        try
        {
            string token = existingToken;

            // Chỉ mở fxlaunch.exe khi THẬT SỰ cần lấy token (thư mục chưa từng
            // đăng nhập, hoặc token đã lưu hoá ra không dùng được nữa — xem
            // nhánh else bên dưới). Nhánh này tồn tại DUY NHẤT để lấy token —
            // có token DÙNG ĐƯỢC thì client đầu tiên mở thẳng bằng helper y
            // như client thứ 2..N.
            //
            // TRƯỚC ĐÂY điều kiện là `runningInFolder == 0 || token rỗng`, nên
            // thư mục ĐÃ có token mà chưa chạy client nào vẫn rơi vào nhánh này:
            // fxlaunch.exe sinh ra client #1, rồi LaunchClientsToTotalAsync bên
            // dưới đếm lại và mở thêm client #2 bằng helper → bấm mở 1 cửa sổ
            // nhưng ra 2 (một lần bấm đi qua HAI đường launch độc lập, đường sau
            // không biết đường trước đã sinh ra client nào).
            //
            // Đã chẩn đoán sai 2 lần trước đó là "đếm client sai" (đổi
            // MainModule → WMI ExecutablePath → WMI CommandLine). Phép đếm vốn
            // KHÔNG sai — ảnh người dùng gửi hiện badge "2/1 cửa sổ" và liệt kê
            // đúng cả 2 PID thuộc thư mục, tức đếm đúng nhưng đã mở dư.
            //
            // 2026-11-16 (FCT-2658/DEC-598): "token" thực chất là ARGUMENT KHỞI ĐỘNG fxgame.exe do
            // fxlaunch.exe sinh ra, đổi theo phiên đăng nhập. Argument đã lưu chỉ được tin sau khi một
            // client mở bằng nó SỐNG trọn cửa sổ quan sát (trước đây chỉ cần "còn sống sau 8 s" —
            // client mang argument sai sống ~60 s rồi chết nên kiểm đó luôn đạt). Quan sát nằm trong
            // LaunchClientsToTotalAsync, áp cho client ĐẦU TIÊN của lượt bất kể nó là client thứ mấy
            // (Codex P1#4: thư mục đã có client cũ, mở thêm đúng 1 cũng phải được kiểm).
            if (string.IsNullOrEmpty(token))
            {
                var login = await LoginViaFxlaunchAndWaitAsync(gameFolder, token, "chưa có argument đã lưu", progress);
                if (!login.Ok) return new EnsureResult(0, login.Message, token);
                // Argument vừa lấy từ launcher: không mở launcher lần nữa trong cùng lần bấm. Client nguồn
                // đã đếm vào số cửa sổ; LaunchClientsToTotalAsync chỉ mở phần còn thiếu.
                var r0 = await LaunchClientsToTotalAsync(gameFolder, login.Token, targetTotal, progress, useLocalExtension,
                    newClientValidThisClick: login.Verdict == ArgumentVerdict.Valid,
                    useSecondaryLocalExtension: useSecondaryLocalExtension);
                if (r0.StaleArgumentPid > 0)
                    return RejectFreshArgument(gameFolder, r0, login.Token, token);
                return new EnsureResult(r0.Launched, r0.Message, login.Token, login.SourceStart ?? r0.ValidSourceStart);
            }

            // Mở bằng argument đã lưu. Client MỚI đầu tiên của lượt LUÔN bị quan sát — client phiên cũ
            // đang chạy không được dùng làm bằng chứng (Codex v2 P1#4).
            var r = await LaunchClientsToTotalAsync(gameFolder, token, targetTotal, progress, useLocalExtension,
                useSecondaryLocalExtension: useSecondaryLocalExtension);
            if (r.StaleArgumentPid == 0)
                return new EnsureResult(r.Launched, r.Message, token, r.ValidSourceStart);

            // B/C: client mới mở bằng argument đã lưu CHẾT hoặc bị WER treo trong cửa sổ quan sát (dấu
            // hiệu đã đo của argument sai). Tắt đúng client đó (PID + StartTime + exe của thư mục), đăng
            // nhập lại qua launcher, bắt argument của PID MỚI của ĐÚNG thư mục, rồi mở nốt. Tối đa một
            // lần mở launcher trong một lần bấm.
            string reason =
                $"client mở bằng argument đã lưu đã thoát/bị treo do lỗi trong {LaunchArgumentLogic.ObserveWindowMs / 1000} giây quan sát";
            DiagnosticLogService.Trace(
                $"[token đã lưu] {gameFolder} — {reason} ({LaunchArgumentLogic.Describe(token)}), " +
                "coi như argument khởi động đã hết hạn, mở fxlaunch để đăng nhập lại");
            KillStaleClient(r.StaleArgumentPid, r.StaleArgumentStart, gameFolder, reason);

            progress?.Report("Argument khởi động đã lưu có vẻ đã hết hạn — mở launcher để đăng nhập lại...");
            var relogin = await LoginViaFxlaunchAndWaitAsync(gameFolder, token, reason, progress);
            // Đăng nhập lại thất bại / client nguồn mới cũng hỏng ⇒ giữ argument CŨ ở caller (không lưu
            // argument hỏng), báo lỗi rõ.
            if (!relogin.Ok) return new EnsureResult(r.Launched, r.Message + " " + relogin.Message, token);

            var r2 = await LaunchClientsToTotalAsync(gameFolder, relogin.Token, targetTotal, progress, useLocalExtension,
                newClientValidThisClick: relogin.Verdict == ArgumentVerdict.Valid,
                useSecondaryLocalExtension: useSecondaryLocalExtension);
            if (r2.StaleArgumentPid > 0)
            {
                var rej = RejectFreshArgument(gameFolder, r2, relogin.Token, token, priorErrors: r.Errors);
                return rej with { Launched = r.Launched + r2.Launched };
            }
            string finalMsg = r.Errors is { Count: > 0 } ? $"{r2.Message} (Lượt trước có lỗi: {string.Join(", ", r.Errors)})" : r2.Message;
            return new EnsureResult(r.Launched + r2.Launched, finalMsg, relogin.Token, relogin.SourceStart ?? r2.ValidSourceStart);
        }
        catch (InvalidOperationException ex) when (useLocalExtension || useSecondaryLocalExtension)
        {
            return new EnsureResult(0, ex.Message, existingToken);
        }
        finally
        {
            LaunchGate.Release();
        }
    }

    /// <summary>
    /// Argument VỪA lấy từ launcher (client nguồn ra Unknown) nhưng client MỚI đầu tiên mở bằng nó lại
    /// chết / bị WER treo ⇒ KHÔNG lưu argument đó (trả lại argument trước đó — Codex v2 P1#3 "Stale ⇒
    /// không lưu, không nhân bản"), tắt đúng client hỏng, không mở launcher lần nữa trong cùng lần bấm.
    /// </summary>
    private static EnsureResult RejectFreshArgument(string gameFolder, LaunchRoundResult round, string freshToken, string previousToken,
        IReadOnlyList<string>? priorErrors = null)
    {
        string reason = "client mở bằng argument vừa lấy qua launcher đã thoát/bị treo do lỗi trong cửa sổ quan sát";
        DiagnosticLogService.Trace(
            $"[argument khởi động] {gameFolder} — {reason}; KHÔNG lưu argument này ({LaunchArgumentLogic.Describe(freshToken)}), " +
            "giữ argument trước đó, dừng (không lặp)");
        KillStaleClient(round.StaleArgumentPid, round.StaleArgumentStart, gameFolder, reason);
        // R4 Minor: giu loi cua CA HAI luot (argument cu + argument vua lay) trong cau bao — neu khong
        // thi loi cua luot launcher bi cau "chua luu argument" che mat.
        var combined = new List<string>();
        if (priorErrors != null) combined.AddRange(priorErrors);
        if (round.Errors != null) combined.AddRange(round.Errors);
        string errSuffix = combined.Count > 0 ? " Lỗi: " + string.Join(", ", combined) : "";
        return new EnsureResult(round.Launched,
            "Client mở bằng argument khởi động vừa lấy qua launcher đã thoát hoặc bị treo do lỗi — chưa lưu argument mới, " +
            "đã dừng mở thêm. Hãy kiểm tra game/launcher rồi bấm MỞ GAME lại." + errSuffix,
            previousToken);
    }

    /// <summary>
    /// Mở fxlaunch.exe của CHÍNH thư mục này, chờ người dùng đăng nhập, đọc argument khởi động từ
    /// fxgame.exe MỚI của ĐÚNG thư mục đó (A), rồi LUÔN quan sát chính PID nguồn đó trọn cửa sổ —
    /// kể cả khi mục tiêu chỉ là 1 cửa sổ (Codex v2 P1#3). Nguồn ra Stale (chết / WER treo) ⇒ Ok=false:
    /// KHÔNG trả argument (caller không lưu), KHÔNG mở thêm, báo lỗi rõ cho UI. Unknown ⇒ Ok=true
    /// (argument do chính launcher vừa sinh, không có bằng chứng hỏng) nhưng các client sau vẫn bị
    /// quan sát. Dùng ở cả hai nơi: thư mục chưa từng đăng nhập, và argument đã lưu hoá ra hết hạn.
    /// </summary>
    private static async Task<(bool Ok, string Token, string Message, ArgumentVerdict Verdict, DateTime? SourceStart)>
        LoginViaFxlaunchAndWaitAsync(string gameFolder, string oldToken, string reason, IProgress<string>? progress)
    {
        progress?.Report("Đang mở launcher, hãy đăng nhập vào game...");
        var pidsBefore = GetFxgamePids();

        var (launcherOk, launcherMsg) = await LaunchFirstClientAsync(gameFolder);
        if (!launcherOk) return (false, string.Empty, launcherMsg, ArgumentVerdict.Unknown, null);

        var (token, sourcePid) = await WaitForTokenInternalAsync(gameFolder, pidsBefore, progress);
        if (string.IsNullOrEmpty(token))
        {
            return (false, string.Empty,
                "Hết thời gian chờ đăng nhập (60s). Sau khi vào game xong, bấm MỞ lại để mở các cửa sổ còn lại.",
                ArgumentVerdict.Unknown, null);
        }

        DateTime? sourceStart = GetFxgameStartTimes(new[] { sourcePid }).TryGetValue(sourcePid, out var st) ? st : null;

        var verdict = await ObserveLaunchedClientAsync(
            sourcePid, "Đang xác nhận client vừa đăng nhập qua launcher", progress);
        if (!LaunchArgumentLogic.AcceptLauncherSource(verdict))
        {
            DiagnosticLogService.Trace(
                $"[argument khởi động] {gameFolder} — client nguồn PID={sourcePid} từ launcher đã thoát/bị treo do lỗi " +
                $"trong cửa sổ quan sát; KHÔNG lưu argument này ({LaunchArgumentLogic.Describe(token)}), không mở thêm");
            return (false, string.Empty,
                "Client vừa đăng nhập qua launcher đã thoát hoặc bị treo do lỗi trong " +
                $"{LaunchArgumentLogic.ObserveWindowMs / 1000} giây — chưa lưu argument khởi động mới, không mở thêm cửa sổ. " +
                "Hãy kiểm tra game/launcher rồi bấm MỞ GAME lại.",
                verdict, null);
        }

        LogArgumentChange($"làm mới qua launcher ({reason}; client nguồn: {verdict})", gameFolder, sourcePid, oldToken, token);
        return (true, token, "Đăng nhập thành công.", verdict, sourceStart);
    }

    /// <summary>
    /// Chờ tới khi lấy được argument khởi động từ fxgame.exe MỚI (không có trong
    /// <paramref name="pidsBefore"/>) của ĐÚNG <paramref name="gameFolder"/> — thử mỗi 2s trong tối
    /// đa 60s để người dùng có thời gian đăng nhập trong launcher. Không bao giờ đọc client cũ
    /// (có thể chính là client mang argument hỏng) hay client của thư mục khác. Chỉ TRẢ VỀ, không
    /// tự lưu — caller (EnsureClientsRunningAsync → ViewModel) ghi vào đúng thư mục.
    /// </summary>
    private static async Task<(string Token, int Pid)> WaitForTokenInternalAsync(
        string gameFolder, HashSet<int> pidsBefore, IProgress<string>? progress)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);

            // Timeout cứng thêm ở NGOÀI, độc lập với EnumerationOptions.Timeout
            // bên trong DetectTokenAsync — WMI trên máy người dùng từng treo
            // hàng chục phút bất kể timeout khai báo (xem trace.log), nên
            // không tin tưởng tuyệt đối vào timeout nội bộ của searcher. Một
            // lượt bị treo chỉ mất tối đa 8s ở đây rồi vòng lặp tự thử lại,
            // KHÔNG để nó chặn cả 60s chờ đăng nhập.
            var detectTask = DetectTokenAsync(gameFolder, pidsBefore, newOnly: true);
            var winner = await Task.WhenAny(detectTask, Task.Delay(8000));
            if (winner != detectTask)
            {
                DiagnosticLogService.Trace($"  DetectTokenAsync treo quá 8s ở lượt {i + 1}, bỏ qua thử lại");
                progress?.Report($"Đang chờ bạn đăng nhập... ({(i + 1) * 2}s)");
                continue;
            }

            var (ok, token, _, pid) = await detectTask;
            if (ok && !string.IsNullOrEmpty(token))
            {
                DiagnosticLogService.Trace($"  lấy được argument khởi động từ PID={pid} sau {(i + 1) * 2}s");
                return (token, pid);
            }

            progress?.Report($"Đang chờ bạn đăng nhập... ({(i + 1) * 2}s)");
        }

        DiagnosticLogService.Trace($"  TIMEOUT 60s — không lấy được argument khởi động của {gameFolder}");
        return (string.Empty, 0);
    }

    /// <summary>Số cửa sổ game (fxgame.exe) đang chạy, toàn hệ thống.</summary>
    public static int CountRunningClients() => CountRunningFxgame();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Đường dẫn exe của mọi fxgame.exe đang chạy, theo PID — đọc bằng
    /// QueryFullProcessImageName (P/Invoke), KHÔNG dùng WMI và KHÔNG dùng
    /// Process.MainModule.FileName.
    ///
    /// LỊCH SỬ (3 lần sửa sai trước khi tới đây):
    /// 1. MainModule — ném exception khi tiến trình khác kiến trúc bit/quyền,
    ///    bị nuốt bởi try/catch → đếm thiếu.
    /// 2. WMI Win32_Process.ExecutablePath — field này CHƯA kịp populate ngay
    ///    lúc tiến trình vừa khởi động (đúng lúc code vừa lấy token và gọi
    ///    hàm đếm) → vẫn đếm thiếu.
    /// 3. WMI Win32_Process.CommandLine (fallback khi ExecutablePath rỗng) —
    ///    tưởng đã ổn, nhưng log trace.log thực tế trên máy người dùng cho
    ///    thấy WMI có thể TREO HÀNG CHỤC PHÚT trước khi trả lỗi ("Call
    ///    cancelled", "Out of memory") — không liên quan gì tới việc đứng
    ///    trên UI thread hay nền, bản thân que ManagementObjectSearcher.Get()
    ///    có thể chặn rất lâu trên máy này (nghi WMI repository/service
    ///    không khoẻ). Đây là nguyên nhân "Đang xử lý..." treo lâu dù đã bọc
    ///    Task.Run() ở tầng ViewModel.
    ///
    /// QueryFullProcessImageName là API cấp thấp, không qua dịch vụ WMI, trả
    /// về ngay lập tức (hoặc lỗi ngay lập tức), hoạt động được cả khi tiến
    /// trình khác kiến trúc bit với app gọi (không như MainModule).
    /// </summary>
    private static Dictionary<int, string> GetFxgameExecutablePaths()
    {
        var result = new Dictionary<int, string>();
        var procs = Process.GetProcessesByName("fxgame");
        try
        {
            foreach (var p in procs)
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (handle == IntPtr.Zero) continue;

                    var sb = new System.Text.StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(handle, 0, sb, ref size) && size > 0)
                        result[p.Id] = sb.ToString(0, (int)size);
                }
                catch { }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        return result;
    }

    /// <summary>
    /// Số cửa sổ game đang chạy CỦA RIÊNG một thư mục — xem
    /// <see cref="GetFxgameExecutablePaths"/> vì sao dùng QueryFullProcessImageName
    /// thay vì WMI hay Process.MainModule.
    /// </summary>
    public static int CountRunningFxgameInFolder(string folder)
    {
        // Cùng định nghĩa "client của thư mục" với đường argument: khớp ĐÚNG đường dẫn exe (v2 P1#1),
        // để đếm và chọn PID không lệch nhau.
        var exe = FindGameExe(folder, "fxgame.exe");
        var paths = GetFxgameExecutablePaths();
        return paths.Values.Count(p => LaunchArgumentLogic.IsGameExeOfFolder(p, folder, exe));
    }

    /// <summary>
    /// Chờ tới khi CÓ ÍT NHẤT MỘT client (trong tập PID cho trước, hoặc bất kỳ
    /// nếu không truyền) đã kết nối vào server game. Dùng sau khi người dùng
    /// đăng nhập client đầu tiên: token xuất hiện ngay lúc fxgame.exe vừa
    /// chạy, nhưng lúc đó nó MỚI BẮT ĐẦU xác thực. Mở client thứ hai ngay sẽ
    /// khiến hai bên cùng xác thực một token → đứt kết nối.
    /// </summary>
    /// <param name="candidatePids">
    /// Giới hạn việc chờ vào đúng các PID này (thường là PID mới xuất hiện từ
    /// lần launch hiện tại) — quan trọng khi có NHIỀU thư mục: nếu không giới
    /// hạn, một client SẴN CÓ của thư mục khác đã kết nối xong sẽ khiến hàm
    /// trả về sớm dù client vừa mở của thư mục này chưa kết nối gì cả.
    /// </param>
    public static async Task WaitForAnyClientConnectedAsync(
        IProgress<string>? progress = null, HashSet<int>? candidatePids = null)
    {
        const int pollInterval = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < ConnectWaitTimeoutMs)
        {
            var pidsToCheck = candidatePids ?? GetFxgamePids();
            foreach (var pid in pidsToCheck)
            {
                if (TcpTableHelper.HasEstablishedPublicConnection(pid))
                {
                    var remote = TcpTableHelper.GetFirstRemoteAddress(pid);
                    DiagnosticLogService.Trace(
                        $"Client đầu (PID={pid}) đã kết nối server ({remote}) sau {sw.ElapsedMilliseconds}ms");
                    return;
                }
            }

            progress?.Report(
                $"Chờ client đầu kết nối vào server ({sw.ElapsedMilliseconds / 1000}s)...");
            await Task.Delay(pollInterval);
        }

        DiagnosticLogService.Trace(
            $"Client đầu chưa kết nối sau {ConnectWaitTimeoutMs}ms — vẫn mở tiếp");
    }

    /// <summary>
    /// Đếm số fxgame.exe đang chạy, giải phóng handle ngay sau khi đếm.
    /// Process.GetProcessesByName cấp phát 1 handle cho MỖI process trả về —
    /// không Dispose sẽ rò rỉ handle, đặc biệt khi gọi lặp trong vòng poll.
    /// </summary>
    private static int CountRunningFxgame()
    {
        var procs = Process.GetProcessesByName("fxgame");
        try
        {
            return procs.Length;
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Tập PID của các fxgame.exe đang chạy.</summary>
    private static HashSet<int> GetFxgamePids()
    {
        var procs = Process.GetProcessesByName("fxgame");
        try
        {
            return procs.Select(p => p.Id).ToHashSet();
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Tiến trình còn sống không. Lỗi truy cập (khác quyền) ⇒ coi như còn sống: không
    /// biết thì không được kết luận argument hỏng.</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { return !p.HasExited; }
            catch { return true; }
        }
        catch (ArgumentException) { return false; }   // không còn PID này
        catch { return true; }
    }

    /// <summary>
    /// Chờ tới khi client (PID cho trước) thật sự thiết lập được kết nối TCP
    /// tới server game, hoặc hết <paramref name="timeoutMs"/>. Đây là tín hiệu
    /// chính xác cho biết nó đã xác thực xong, thay vì đoán mò bằng số giây
    /// cố định. Trả về true/false để caller BIẾT được có kết nối hay không
    /// (khác <see cref="WaitForClientConnectedAsync"/> — hàm đó chỉ log rồi
    /// vẫn mở tiếp, không cần biết kết quả).
    /// </summary>
    private static async Task<bool> WaitForClientConnectedWithResultAsync(
        int pid, int timeoutMs, string waitingMessagePrefix, IProgress<string>? progress)
    {
        const int pollInterval = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (TcpTableHelper.HasEstablishedPublicConnection(pid))
            {
                var remote = TcpTableHelper.GetFirstRemoteAddress(pid);
                DiagnosticLogService.Trace(
                    $"  PID={pid} đã kết nối server ({remote}) sau {sw.ElapsedMilliseconds}ms");
                return true;
            }

            // Client đã chết thì chờ tiếp tới 60 s là vô ích — argument hỏng thường kết thúc
            // bằng cảnh này (FCT-2658: treo ~60 s rồi chết).
            if (!IsProcessAlive(pid))
            {
                DiagnosticLogService.Trace(
                    $"  PID={pid} đã thoát sau {sw.ElapsedMilliseconds}ms, chưa từng có kết nối server — {TunnelProbe.Current()}");
                return false;
            }

            progress?.Report($"{waitingMessagePrefix} ({sw.ElapsedMilliseconds / 1000}s)...");
            await Task.Delay(pollInterval);
        }

        // Chan doan thay im lang: 124/615 ket qua trong trace.log la loai "khong thay" ma khong co mo ta nao,
        // trong khi 491 dong thanh cong lai ghi ro dich ⇒ that bai khong quy duoc nguyên nhân (batch 55).
        DiagnosticLogService.Trace(
            $"  PID={pid} chưa thấy kết nối server sau {timeoutMs}ms — {TunnelProbe.Current()} | {TcpTableHelper.DescribeConnections(pid)} | {TcpTableHelper.DescribeForwarderChain(pid)}");
        return false;
    }

    /// <summary>
    /// Chờ tới khi client (PID cho trước) thật sự thiết lập được kết nối TCP
    /// tới server game. Đây là tín hiệu chính xác cho biết nó đã xác thực
    /// xong, thay vì đoán mò bằng số giây cố định.
    /// </summary>
    private static async Task WaitForClientConnectedAsync(
        int pid, int openedSoFar, int targetTotal, IProgress<string>? progress)
    {
        await WaitForClientConnectedWithResultAsync(
            pid, ConnectWaitTimeoutMs,
            $"Đã mở {openedSoFar}/{targetTotal} cửa sổ — chờ client vừa mở kết nối vào server",
            progress);
    }

    // ── Quản lý client đang chạy ─────────────────────────────
    /// <param name="knownFolders">
    /// Danh sách thư mục đã cấu hình để gán RunningClient.FolderPath. Truyền
    /// null/rỗng thì giữ hành vi cũ (không resolve, FolderPath luôn null —
    /// mọi client rơi vào bucket "không rõ thư mục" khi hiển thị theo nhóm).
    /// </param>
    /// <remarks>
    /// Dùng QueryFullProcessImageName (P/Invoke, xem GetFxgameExecutablePaths)
    /// để lấy đường dẫn — KHÔNG dùng WMI. Hàm này bị DispatcherTimer gọi mỗi 2
    /// GIÂY TRÊN UI THREAD (MultiClientViewModel.RefreshRunning); WMI
    /// (ManagementObjectSearcher) từng được thử ở đây rồi bị gỡ vì có thể treo
    /// hàng chục phút trên máy người dùng thực tế (xem trace.log: nhiều dòng
    /// "Call cancelled"/"Out of memory" — không phải giả thuyết). Cũng KHÔNG
    /// dùng Process.MainModule vì nó ném exception với một số tiến trình
    /// (khác kiến trúc bit/quyền) — QueryFullProcessImageName không có cả 2
    /// vấn đề: nhanh, tất định, không treo, hoạt động cross-bitness.
    /// </remarks>
    public static List<RunningClient> GetRunningClients(IReadOnlyList<string>? knownFolders = null)
    {
        var result = new List<RunningClient>();
        var exePaths = (knownFolders != null && knownFolders.Count > 0)
            ? GetFxgameExecutablePaths()
            : new Dictionary<int, string>();
        // KHÔNG gọi FindGameExe ở đây: hàm này chạy mỗi 2 s TRÊN UI THREAD, mà FindGameExe có thể đào 2
        // cấp thư mục (I/O đĩa) — ba vị trí chuẩn (gốc/bin64/bin) đã đủ cho mọi bản cài đã đo. Bản cài
        // lạ không khớp thì client rơi vào "không rõ thư mục" — an toàn hơn gán nhầm cho dòng khác.
        IReadOnlyDictionary<string, string?>? findExe = null;
        try
        {
            var processes = Process.GetProcessesByName("fxgame");
            foreach (var p in processes)
            {
                try
                {
                    bool hasExited = false;
                    try
                    {
                        hasExited = p.HasExited;
                    }
                    catch
                    {
                        hasExited = true;
                    }

                    if (hasExited) continue;

                    string startTime = "Vừa mở";
                    try { startTime = p.StartTime.ToString("HH:mm:ss"); } catch { }

                    string? folder = null;
                    if (knownFolders != null && knownFolders.Count > 0 && exePaths.TryGetValue(p.Id, out var exePath))
                    {
                        folder = LaunchArgumentLogic.ResolveOwningFolder(exePath, knownFolders, findExe);
                    }

                    result.Add(new RunningClient
                    {
                        Pid        = p.Id,
                        StartTime  = startTime,
                        IsVisible  = WindowHelper.IsClientVisible(p.Id),
                        FolderPath = folder
                    });
                }
                catch
                {
                    // Bỏ qua tiến trình vừa tạo hoặc không có quyền truy cập
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch { }
        return result;
    }

    public static bool KillClient(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            bool hasExited = false;
            try
            {
                hasExited = p.HasExited;
            }
            catch
            {
                hasExited = true;
            }

            if (!hasExited)
            {
                p.Kill(entireProcessTree: true);
            }
            return true;
        }
        catch { return false; }
    }
}
