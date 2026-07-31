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
    public string GameFolder  { get; set; } = string.Empty;
    public string SavedAt     { get; set; } = string.Empty;
}

public class RunningClient
{
    public int    Pid       { get; set; }
    public string StartTime { get; set; } = string.Empty;
    // Hỏi thẳng WinAPI (WindowHelper.IsClientVisible) mỗi lần refresh thay vì
    // tự lưu cờ nội bộ — tránh lệch trạng thái nếu app tắt/mở lại giữa lúc ẩn.
    public bool   IsVisible { get; set; } = true;
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

        int alreadyRunning = CountRunningFxgame();
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

    /// <summary>Số cửa sổ game (fxgame.exe) đang chạy.</summary>
    public static int CountRunningClients() => CountRunningFxgame();

    /// <summary>
    /// Chờ tới khi CÓ ÍT NHẤT MỘT client đã kết nối vào server game. Dùng sau
    /// khi người dùng đăng nhập client đầu tiên: token xuất hiện ngay lúc
    /// fxgame.exe vừa chạy, nhưng lúc đó nó MỚI BẮT ĐẦU xác thực. Mở client
    /// thứ hai ngay sẽ khiến hai bên cùng xác thực một token → đứt kết nối.
    /// </summary>
    public static async Task WaitForAnyClientConnectedAsync(IProgress<string>? progress = null)
    {
        const int pollInterval = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < ConnectWaitTimeoutMs)
        {
            foreach (var pid in GetFxgamePids())
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
    public static List<RunningClient> GetRunningClients()
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

                    result.Add(new RunningClient
                    {
                        Pid       = p.Id,
                        StartTime = startTime,
                        IsVisible = WindowHelper.IsClientVisible(p.Id)
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
