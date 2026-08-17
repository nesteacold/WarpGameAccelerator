using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace WarpGameAccelerator.Services;

public class UpdateService
{
    private const string GitHubRepo = "nesteacold/WarpGameAccelerator";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

    public async Task<(bool HasUpdate, string Version, string DownloadUrl)> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "WarpGameAccelerator-Updater");

            var response = await client.GetStringAsync(GitHubApiUrl);
            var json = JsonNode.Parse(response);
            if (json == null) return (false, "", "");

            var tagName = json["tag_name"]?.ToString() ?? ""; // e.g. "v1.5.0"
            var cleanTag = tagName.TrimStart('v', 'V');
            
            if (Version.TryParse(cleanTag, out Version? latestVersion))
            {
                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (currentVersion != null && latestVersion > currentVersion)
                {
                    // Find the .exe asset
                    var assets = json["assets"]?.AsArray();
                    if (assets != null)
                    {
                        foreach (var asset in assets)
                        {
                            var name = asset?["name"]?.ToString() ?? "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                var downloadUrl = asset?["browser_download_url"]?.ToString() ?? "";
                                return (true, tagName, downloadUrl);
                            }
                        }
                    }
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Repo has no releases yet -> treated as latest
            return (false, "latest", "");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking for updates: {ex.Message}");
        }

        return (false, "latest", "");
    }

    /// <summary>Kích thước tối thiểu hợp lệ của bản build self-contained — dưới mức này
    /// gần như chắc chắn là tải lỗi/trang HTML lỗi, KHÔNG được ghi đè lên exe đang chạy.</summary>
    private const long MinValidExeBytes = 10L * 1024 * 1024;

    /// <returns>Thông báo lỗi nếu không khởi động được quy trình cập nhật; null nếu đã bàn giao cho script và app sắp thoát.</returns>
    public async Task<string?> DownloadAndInstallUpdateAsync(string downloadUrl)
    {
        try
        {
            var tempExePath = Path.Combine(Path.GetTempPath(), "WarpGameAccelerator_Update.exe");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "WarpGameAccelerator-Updater");
            var fileBytes = await client.GetByteArrayAsync(downloadUrl);

            // Tải hụt/tải nhầm trang lỗi mà vẫn ghi đè = phá hỏng bản đang chạy.
            if (fileBytes.LongLength < MinValidExeBytes)
                return $"File tải về không hợp lệ ({fileBytes.LongLength / 1024 / 1024} MB) — huỷ cập nhật.";

            await File.WriteAllBytesAsync(tempExePath, fileBytes);

            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath))
                return "Không xác định được đường dẫn app đang chạy — huỷ cập nhật.";

            var pid = Environment.ProcessId;
            var batPath = Path.Combine(Path.GetTempPath(), "update_warp.bat");

            // BUG ĐÃ SỬA: bản cũ chỉ `timeout /t 2` rồi copy đè MỘT lần và KHÔNG
            // kiểm tra kết quả. File exe self-contained cỡ trăm MB nên rất dễ còn
            // bị khoá sau 2 giây (tiến trình cũ chưa thoát hẳn, hoặc antivirus đang
            // quét) → `copy` thất bại IM LẶNG rồi script vẫn khởi động lại đúng
            // file cũ → đúng triệu chứng "cập nhật xong vẫn mở ra bản cũ, phải bấm
            // cập nhật lần 2". Nay: chờ đúng PID cũ thoát hẳn, retry copy tới khi
            // được, và báo lỗi rõ ràng nếu thất bại thay vì âm thầm chạy bản cũ.
            var batContent = $@"@echo off
title WARP Game Accelerator Updater
echo Dang cai dat ban cap nhat, vui long doi...

rem ── Chờ tiến trình cũ (PID {pid}) thoát hẳn, tối đa ~60s ──
set /a waited=0
:waitloop
tasklist /FI ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if errorlevel 1 goto copyfile
set /a waited+=1
if %waited% GEQ 60 goto copyfile
timeout /t 1 /nobreak >nul
goto waitloop

rem ── Retry copy tới khi ghi đè được (khoá file có thể còn nán lại) ──
:copyfile
set /a tries=0
:copyloop
copy /y ""{tempExePath}"" ""{currentExePath}"" >nul 2>&1
if not errorlevel 1 goto launch
set /a tries+=1
if %tries% GEQ 30 goto failed
timeout /t 1 /nobreak >nul
goto copyloop

:launch
powershell -Command ""Start-Process -FilePath '{currentExePath}' -Verb RunAs""
del ""{tempExePath}"" >nul 2>&1
del ""%~f0""
exit

:failed
echo.
echo CAP NHAT THAT BAI: khong ghi de duoc file (dang bi khoa).
echo Hay dong han app (ke ca system tray) roi bam Cap nhat lai.
echo.
pause
powershell -Command ""Start-Process -FilePath '{currentExePath}' -Verb RunAs""
del ""%~f0""
";
            await File.WriteAllTextAsync(batPath, batContent);

            var startInfo = new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(startInfo);

            // Không kill mihomo.exe ở đây — để tunnel WireGuard tiếp tục chạy
            // xuyên suốt lúc app tự restart, tránh game bị rớt kết nối giữa chừng.
            Environment.Exit(0);
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error downloading/installing update: {ex.Message}");
            return $"Lỗi khi tải bản cập nhật: {ex.Message}";
        }
    }
}
