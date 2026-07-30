// ============================================================
// Services/DxvkBoosterService.cs
// Orchestrate AOW_DXVK302_Launcher.exe (dự án DXVK_Project/installer/AOWLauncher,
// do agent khác phụ trách) từ UI của WarpGameAccelerator, KHÔNG sửa file của
// project đó — chỉ điều khiển menu CLI có sẵn của nó qua stdin, giống hệt
// người dùng gõ tay. "uninstall" có sẵn CLI arg riêng nên gọi thẳng; "cài đặt"
// và "dọn log" chỉ tồn tại dưới dạng lựa chọn menu tương tác ([1]/[3]) nên
// phải giả lập nhập liệu qua RedirectStandardInput.
// ============================================================
using System.Diagnostics;
using System.IO;

namespace WarpGameAccelerator.Services;

public static class DxvkBoosterService
{
    private const string LauncherExeName = "AOW_DXVK302_Launcher.exe";
    // Phải khớp chính xác MarkerFileName trong AOWLauncher/Program.cs.
    private const string MarkerFileName = "dxvk302_install_marker.txt";

    private static readonly string DataFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "WarpGameAccelerator", "Data", "dxvk_booster.json");

    public static async Task<string?> LoadSavedFolderAsync()
    {
        try
        {
            if (!File.Exists(DataFilePath)) return null;
            var json = await File.ReadAllTextAsync(DataFilePath);
            var info = System.Text.Json.JsonSerializer.Deserialize<BoosterFolderInfo>(json);
            return info?.GameFolder;
        }
        catch { return null; }
    }

    public static async Task SaveFolderAsync(string gameFolder)
    {
        try
        {
            var dir = Path.GetDirectoryName(DataFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(new BoosterFolderInfo { GameFolder = gameFolder });
            await File.WriteAllTextAsync(DataFilePath, json);
        }
        catch { }
    }

    public static bool IsInstalled(string gameFolder)
        => File.Exists(Path.Combine(gameFolder, MarkerFileName));

    public static (bool Valid, string Message) ValidateGameFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return (false, "Thư mục không tồn tại.");

        bool hasBin64 = Directory.Exists(Path.Combine(folder, "bin64"));
        return hasBin64
            ? (true, "✓  Tìm thấy bin64\\")
            : (false, "Không tìm thấy bin64\\ — chọn đúng thư mục gốc cài đặt AOW.");
    }

    // Giải nén lại mỗi lần chạy (file chỉ ~500KB, đảm bảo luôn dùng đúng bản
    // launcher đóng gói trong WarpGameAccelerator, không cần version-check
    // như mihomo.exe/wgcf.exe vốn nặng chục MB).
    private static string ExtractLauncherExe(string gameFolder)
    {
        var destPath = Path.Combine(gameFolder, LauncherExeName);
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resName = assembly.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith("." + LauncherExeName, StringComparison.OrdinalIgnoreCase));

        if (resName == null)
            throw new FileNotFoundException($"Không tìm thấy embedded resource {LauncherExeName}");

        using var stream = assembly.GetManifestResourceStream(resName)
            ?? throw new FileNotFoundException($"Không đọc được embedded resource {LauncherExeName}");
        using var fileStream = File.Create(destPath);
        stream.CopyTo(fileStream);
        return destPath;
    }

    private static async Task<(bool Success, string Output)> RunMenuChoiceAsync(string gameFolder, string menuChoice)
    {
        var exePath = ExtractLauncherExe(gameFolder);

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            WorkingDirectory       = gameFolder,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // menuChoice → Pause() ReadLine() → "0" thoát vòng lặp menu.
        await process.StandardInput.WriteLineAsync(menuChoice);
        await process.StandardInput.WriteLineAsync(); // Pause()
        await process.StandardInput.WriteLineAsync("0");
        process.StandardInput.Close();

        var waitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, output + "\n[TIMEOUT] Launcher không phản hồi sau 60s.");
        }

        return (process.ExitCode == 0, output.ToString());
    }

    public static Task<(bool Success, string Output)> InstallAsync(string gameFolder)
        => RunMenuChoiceAsync(gameFolder, "1");

    public static Task<(bool Success, string Output)> CleanLogsAsync(string gameFolder)
        => RunMenuChoiceAsync(gameFolder, "3");

    public static async Task<(bool Success, string Output)> UninstallAsync(string gameFolder)
    {
        var exePath = ExtractLauncherExe(gameFolder);

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            WorkingDirectory       = gameFolder,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("uninstall");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var waitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, output + "\n[TIMEOUT] Launcher không phản hồi sau 30s.");
        }

        return (process.ExitCode == 0, output.ToString());
    }

    private class BoosterFolderInfo
    {
        public string GameFolder { get; set; } = string.Empty;
    }
}
