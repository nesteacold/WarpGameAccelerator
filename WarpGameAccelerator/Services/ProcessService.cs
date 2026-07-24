// ============================================================
// Services/ProcessService.cs — Liệt kê running processes
// ============================================================
using System.Diagnostics;
using WarpGameAccelerator.Models;

namespace WarpGameAccelerator.Services;

public class ProcessService
{
    /// <summary>Lấy danh sách processes đang chạy (lọc bỏ system process)</summary>
    public IReadOnlyList<GameProcess> GetRunningProcesses()
    {
        var processes = new List<GameProcess>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id <= 4) continue; // bỏ System, Idle
                if (string.IsNullOrWhiteSpace(proc.ProcessName)) continue;

                string exePath = string.Empty;
                try { exePath = proc.MainModule?.FileName ?? string.Empty; }
                catch { /* access denied cho system procs */ }

                var key = proc.ProcessName.ToLowerInvariant();
                if (!seen.Add(key)) continue; // dedup

                processes.Add(new GameProcess
                {
                    ProcessName = proc.ProcessName,
                    ExePath     = exePath,
                    ProcessId   = proc.Id,
                    IsSelected  = false
                });
            }
            catch { /* ignore */ }
        }

        return processes
            .OrderBy(p => p.ProcessName)
            .ToList()
            .AsReadOnly();
    }
}
