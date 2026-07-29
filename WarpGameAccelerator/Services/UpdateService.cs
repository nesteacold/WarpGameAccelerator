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

    public async Task DownloadAndInstallUpdateAsync(string downloadUrl)
    {
        try
        {
            var tempExePath = Path.Combine(Path.GetTempPath(), "WarpGameAccelerator_Update.exe");
            
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "WarpGameAccelerator-Updater");
            var fileBytes = await client.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempExePath, fileBytes);

            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath)) return;

            var batPath = Path.Combine(Path.GetTempPath(), "update_warp.bat");
            var batContent = $@"@echo off
title WARP Game Accelerator Updater
echo Đang cài đặt bản cập nhật, vui lòng đợi...
timeout /t 2 /nobreak > nul
copy /y ""{tempExePath}"" ""{currentExePath}""
powershell -Command ""Start-Process -FilePath '{currentExePath}' -Verb RunAs""
del ""{tempExePath}""
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error downloading/installing update: {ex.Message}");
        }
    }
}
