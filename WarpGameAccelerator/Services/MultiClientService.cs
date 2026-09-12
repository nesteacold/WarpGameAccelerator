// ============================================================
// Services/MultiClientService.cs
// Quản lý việc mở nhiều client game AOW (Age of Wushu)
// ============================================================
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
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
public record GameFolderEntry(string Path, int LastClientCount, DateTimeOffset AddedAt, string Token = "");

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

    /// <summary>
    /// Giới hạn thời gian chờ một client kết nối vào server. Hết giờ thì vẫn
    /// mở tiếp thay vì treo — có thể người dùng chưa chọn nhân vật/vào game.
    /// </summary>
    private const int ConnectWaitTimeoutMs = 60000;

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

        // 2. Nếu không thấy, quét đệ quy subfolder
        try
        {
            var files = Directory.GetFiles(folder, exeName, SearchOption.AllDirectories);
            if (files.Length > 0) return files[0];
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
        try
        {
            if (File.Exists(GameFoldersFilePath))
            {
                var json = File.ReadAllText(GameFoldersFilePath);
                var loaded = JsonSerializer.Deserialize<List<GameFolderEntry>>(json);
                if (loaded != null) return loaded;
                return new List<GameFolderEntry>();
            }

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
                return seeded;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"LoadGameFolders EXCEPTION: {ex}");
        }
        return new List<GameFolderEntry>();
    }

    public static void SaveGameFolders(List<GameFolderEntry> folders)
    {
        try
        {
            var dir = Path.GetDirectoryName(GameFoldersFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(GameFoldersFilePath,
                JsonSerializer.Serialize(folders, new JsonSerializerOptions { WriteIndented = true }));
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

    /// <summary>
    /// Thư mục (trong danh sách đã biết) sở hữu file exe này, so theo tiền tố
    /// đường dẫn đã chuẩn hoá — khớp cả trường hợp exe nằm trong bin64/Bin64.
    /// </summary>
    private static string? ResolveOwningFolder(string exePath, IReadOnlyList<string> knownFolders)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var fullExe = Path.GetFullPath(exePath);
            foreach (var folder in knownFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                string fullFolder;
                try { fullFolder = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
                catch { continue; }

                if (fullExe.Equals(fullFolder, StringComparison.OrdinalIgnoreCase) ||
                    fullExe.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
            }
        }
        catch { }
        return null;
    }

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

    // ── Bước 2: Detect token từ fxgame.exe đang chạy ────────
    public static async Task<(bool Success, string Token, string Message)> DetectTokenAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'fxgame.exe'");
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        string? cmdLine = null;
                        try
                        {
                            cmdLine = obj["CommandLine"]?.ToString();
                        }
                        catch
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(cmdLine)) continue;

                        // CommandLine: "C:\...\fxgame.exe" TOKEN
                        // Tách token ra khỏi phần exe path
                        string token = ParseTokenFromCommandLine(cmdLine);
                        if (!string.IsNullOrEmpty(token))
                            return (true, token, "Detect token thành công!");
                    }
                }

                return (false, string.Empty,
                    "Không tìm thấy fxgame.exe đang chạy. Hãy mở client đầu tiên trước.");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Lỗi WMI: {ex.Message}");
            }
        });
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
    public static async Task<(int Launched, string Message)> LaunchClientsToTotalAsync(
        string gameFolder, string token, int targetTotal, IProgress<string>? progress = null)
    {
        var gamePath = FindGameExe(gameFolder, "fxgame.exe");
        if (gamePath == null)
            return (0, "Không tìm thấy fxgame.exe.");

        // Đếm THEO THƯ MỤC (không phải toàn hệ thống) — hỗ trợ nhiều thư mục
        // chạy song song mà không cộng dồn số cửa sổ của nhau.
        int alreadyRunning = CountRunningFxgameInFolder(gameFolder);
        int count = targetTotal - alreadyRunning;

        DiagnosticLogService.Trace(
            $"LaunchClientsToTotal — mục tiêu {targetTotal}, đang chạy {alreadyRunning} → cần mở thêm {count}");

        if (count <= 0)
            return (0, $"Đã có đủ {alreadyRunning}/{targetTotal} cửa sổ game đang chạy.");

        int launched = 0;
        var errors   = new List<string>();

        for (int i = 0; i < count; i++)
        {
            try
            {
                var pidsBefore = GetFxgamePids();
                DiagnosticLogService.Trace($"[client {i + 1}/{count}] trước khi start: {pidsBefore.Count} fxgame đang chạy");

                var launchedAt = System.Diagnostics.Stopwatch.StartNew();

                // Mở qua helper, KHÔNG gọi Process.Start(fxgame) trực tiếp:
                // fxgame.exe giết tiến trình cha của nó (cơ chế tự đóng
                // launcher của game) — nếu gọi thẳng thì app này bị giết.
                progress?.Report($"Đang mở cửa sổ {alreadyRunning + i + 1}/{targetTotal}...");
                LauncherHelper.LaunchGameViaHelper(gamePath, token);
                launched++;
                DiagnosticLogService.Trace($"[client {i + 1}/{count}] đã gọi helper, chờ tiến trình xuất hiện...");

                int newPid = await WaitForNewFxgamePidAsync(pidsBefore, timeoutMs: 15000);

                if (i < count - 1)
                {
                    // Chờ client vừa mở KẾT NỐI XONG vào server rồi mới mở cái
                    // tiếp theo. Hai client cùng xác thực một token gần như
                    // đồng thời sẽ khiến một cái bị "Mạng đứt kết nối".
                    if (newPid > 0)
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

        string msg = launched == count
            ? $"Đã mở đủ {alreadyRunning + launched}/{targetTotal} cửa sổ game!"
            : $"Mở được {alreadyRunning + launched}/{targetTotal} cửa sổ. Lỗi: {string.Join(", ", errors)}";

        DiagnosticLogService.Trace($"LaunchClientsToTotal KẾT THÚC — {alreadyRunning + launched}/{targetTotal}");
        return (launched, msg);
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
    public static async Task<(int Launched, string Message, string Token)> EnsureClientsRunningAsync(
        string gameFolder, string existingToken, int targetTotal, IProgress<string>? progress = null)
    {
        await LaunchGate.WaitAsync();
        try
        {
            string token = existingToken;
            int runningInFolder = CountRunningFxgameInFolder(gameFolder);

            if (runningInFolder == 0 || string.IsNullOrEmpty(token))
            {
                progress?.Report("Đang mở launcher, hãy đăng nhập vào game...");
                var pidsBefore = GetFxgamePids();

                var (launcherOk, launcherMsg) = await LaunchFirstClientAsync(gameFolder);
                if (!launcherOk) return (0, launcherMsg, token);

                if (string.IsNullOrEmpty(token))
                {
                    token = await WaitForTokenInternalAsync(progress);
                    if (string.IsNullOrEmpty(token))
                    {
                        return (0,
                            "Hết thời gian chờ đăng nhập (60s). Sau khi vào game xong, bấm MỞ lại để mở các cửa sổ còn lại.",
                            token);
                    }
                }

                // Chờ đúng client MỚI vừa mở kết nối xong trước khi mở tiếp —
                // giới hạn theo PID mới để không bị "ăn ké" trạng thái đã kết
                // nối sẵn của một thư mục KHÁC đang chạy song song.
                if (targetTotal > CountRunningFxgameInFolder(gameFolder))
                {
                    int newPid = await WaitForNewFxgamePidAsync(pidsBefore, timeoutMs: 15000);
                    if (newPid > 0)
                        await WaitForClientConnectedAsync(newPid, 1, targetTotal, progress);
                    else
                        await WaitForAnyClientConnectedAsync(progress);
                }
            }

            var (launched, msg) = await LaunchClientsToTotalAsync(gameFolder, token, targetTotal, progress);
            return (launched, msg, token);
        }
        finally
        {
            LaunchGate.Release();
        }
    }

    /// <summary>
    /// Chờ tới khi lấy được token từ fxgame.exe đang chạy — thử mỗi 2s trong
    /// tối đa 60s để người dùng có thời gian đăng nhập trong launcher. Chỉ
    /// TRẢ VỀ token, không tự lưu ở đây — token giờ thuộc về đúng thư mục vừa
    /// gọi (per-folder), nên caller (EnsureClientsRunningAsync → ViewModel)
    /// mới biết phải ghi vào GameFolderEntry.Token của thư mục nào.
    /// </summary>
    private static async Task<string> WaitForTokenInternalAsync(IProgress<string>? progress)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);

            var (ok, token, _) = await DetectTokenAsync();
            if (ok && !string.IsNullOrEmpty(token))
            {
                DiagnosticLogService.Trace($"  lấy được token sau {(i + 1) * 2}s");
                return token;
            }

            progress?.Report($"Đang chờ bạn đăng nhập... ({(i + 1) * 2}s)");
        }

        DiagnosticLogService.Trace("  TIMEOUT 60s — không lấy được token");
        return string.Empty;
    }

    /// <summary>Số cửa sổ game (fxgame.exe) đang chạy, toàn hệ thống.</summary>
    public static int CountRunningClients() => CountRunningFxgame();

    /// <summary>
    /// Số cửa sổ game đang chạy CỦA RIÊNG một thư mục — dựa vào
    /// Process.MainModule.FileName của từng fxgame.exe. Tiến trình không đọc
    /// được MainModule (khác kiến trúc/quyền) bị bỏ qua khỏi phép đếm này;
    /// đây là best-effort, không có cách nào đáng tin cậy hơn để quy tiến
    /// trình về thư mục khi không attribute được module path.
    /// </summary>
    public static int CountRunningFxgameInFolder(string folder)
    {
        var procs = Process.GetProcessesByName("fxgame");
        try
        {
            int count = 0;
            var folders = new[] { folder };
            foreach (var p in procs)
            {
                try
                {
                    var exePath = p.MainModule?.FileName;
                    if (exePath != null && ResolveOwningFolder(exePath, folders) != null) count++;
                }
                catch { }
            }
            return count;
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
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

    /// <summary>
    /// Chờ một fxgame.exe MỚI xuất hiện và trả về PID của nó (0 nếu hết giờ).
    /// </summary>
    private static async Task<int> WaitForNewFxgamePidAsync(HashSet<int> pidsBefore, int timeoutMs)
    {
        const int pollInterval = 500;

        for (int elapsed = 0; elapsed < timeoutMs; elapsed += pollInterval)
        {
            await Task.Delay(pollInterval);

            try
            {
                var newPid = GetFxgamePids().FirstOrDefault(pid => !pidsBefore.Contains(pid));
                if (newPid != 0)
                {
                    DiagnosticLogService.Trace($"  client mới PID={newPid} xuất hiện sau {elapsed + pollInterval}ms");
                    return newPid;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Trace($"  WaitForNewFxgamePid EXCEPTION: {ex.Message}");
            }
        }

        DiagnosticLogService.Trace($"  TIMEOUT {timeoutMs}ms — chưa thấy client mới, vẫn tiếp tục");
        return 0;
    }

    /// <summary>
    /// Chờ tới khi client (PID cho trước) thật sự thiết lập được kết nối TCP
    /// tới server game. Đây là tín hiệu chính xác cho biết nó đã xác thực
    /// xong, thay vì đoán mò bằng số giây cố định.
    /// </summary>
    private static async Task WaitForClientConnectedAsync(
        int pid, int openedSoFar, int targetTotal, IProgress<string>? progress)
    {
        const int pollInterval = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < ConnectWaitTimeoutMs)
        {
            if (TcpTableHelper.HasEstablishedPublicConnection(pid))
            {
                var remote = TcpTableHelper.GetFirstRemoteAddress(pid);
                DiagnosticLogService.Trace(
                    $"  PID={pid} đã kết nối server ({remote}) sau {sw.ElapsedMilliseconds}ms");
                return;
            }

            progress?.Report(
                $"Đã mở {openedSoFar}/{targetTotal} cửa sổ — " +
                $"chờ client vừa mở kết nối vào server ({sw.ElapsedMilliseconds / 1000}s)...");

            await Task.Delay(pollInterval);
        }

        DiagnosticLogService.Trace(
            $"  PID={pid} chưa thấy kết nối server sau {ConnectWaitTimeoutMs}ms — vẫn mở tiếp");
    }

    // ── Quản lý client đang chạy ─────────────────────────────
    /// <param name="knownFolders">
    /// Danh sách thư mục đã cấu hình để gán RunningClient.FolderPath. Truyền
    /// null/rỗng thì giữ hành vi cũ (không resolve, FolderPath luôn null —
    /// mọi client rơi vào bucket "không rõ thư mục" khi hiển thị theo nhóm).
    /// </param>
    public static List<RunningClient> GetRunningClients(IReadOnlyList<string>? knownFolders = null)
    {
        var result = new List<RunningClient>();
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
                    if (knownFolders != null && knownFolders.Count > 0)
                    {
                        string? exePath = null;
                        try { exePath = p.MainModule?.FileName; } catch { /* khác kiến trúc/quyền — bỏ qua */ }
                        if (exePath != null) folder = ResolveOwningFolder(exePath, knownFolders);
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
