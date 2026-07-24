// ============================================================
// Helpers/StartupHelper.cs — Registry auto-start manager
// ============================================================
using Microsoft.Win32;

namespace WarpGameAccelerator.Helpers;

public static class StartupHelper
{
    private const string RunKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WarpGameAccelerator";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is not null;
        }
        catch { return false; }
    }

    public static void EnableAutoStart()
    {
        try
        {
            var exePath = Environment.ProcessPath
                          ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            key?.SetValue(AppName, $"\"{exePath}\"");
        }
        catch { /* log hoặc bỏ qua */ }
    }

    public static void DisableAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { }
    }
}
