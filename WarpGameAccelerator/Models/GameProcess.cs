// ============================================================
// Models/GameProcess.cs — Thông tin process game được chọn
// ============================================================
namespace WarpGameAccelerator.Models;

public class GameProcess
{
    public string ProcessName { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool IsSelected { get; set; }

    public string DisplayName => string.IsNullOrEmpty(ExePath)
        ? ProcessName
        : System.IO.Path.GetFileName(ExePath);
}
