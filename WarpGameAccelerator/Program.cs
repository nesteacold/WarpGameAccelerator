// ============================================================
// Program.cs — Entry point tự viết (thay cho Main auto-generated
// của WinUI, đã tắt bằng DISABLE_XAML_GENERATED_MAIN).
//
// Cùng một file .exe phục vụ 2 vai trò:
//   1. Chế độ bình thường  → khởi tạo WinUI, hiện cửa sổ app.
//   2. Chế độ helper       → chỉ mở game rồi đứng làm tiến trình
//      cha "thế mạng", KHÔNG khởi tạo UI/DI/Mihomo gì cả.
// ============================================================
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ── Chế độ helper ────────────────────────────────────
        // Phải chặn ở đây, TRƯỚC khi new App() — nếu để App khởi tạo thì
        // MihomoService trong DI sẽ chạy và kill mihomo.exe của app chính.
        if (args.Length >= 2 && args[0] == LauncherHelper.HelperArgument)
        {
            LauncherHelper.RunAsGameParent(
                gamePath: args[1],
                token:    args.Length > 2 ? args[2] : string.Empty);
            return;
        }

        // ── Chặn đa instance ──────────────────────────────────
        // Phải chặn TRƯỚC Application.Start — nếu để App khởi tạo thì
        // ExtractCoreResources()/StopProxy() sẽ kill mihomo.exe của
        // instance đang chạy trước đó. Giữ mutex sống suốt vòng đời app
        // bằng cách không Dispose cho tới sau khi Application.Start trả về.
        if (!SingleInstanceGuard.TryAcquire(out var singleInstanceMutex))
        {
            SingleInstanceGuard.ShowAlreadyRunningMessage();
            return;
        }

        // ── Chế độ bình thường: khởi động WinUI ──────────────
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        singleInstanceMutex?.ReleaseMutex();
        singleInstanceMutex?.Dispose();
    }
}
