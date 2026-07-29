// ============================================================
// App.xaml.cs — DI setup, App lifecycle
// ============================================================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.ViewModels;
using WinRT.Interop;

namespace WarpGameAccelerator;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    public static IServiceProvider Services { get; private set; } = null!;
    public static DispatcherQueue? DispatcherQueue { get; private set; }

    /// <summary>HWND của MainWindow — cần cho FileOpenPicker</summary>
    public IntPtr MainWindowHandle { get; private set; }

    public App()
    {
        var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpGameAccelerator", "Logs");
        try { if (!System.IO.Directory.Exists(logDir)) System.IO.Directory.CreateDirectory(logDir); } catch { }
        string logFile = System.IO.Path.Combine(logDir, "crash.log");

        // Đánh dấu mốc khởi động. Nếu lần chạy sau thấy trace.log kết thúc mà
        // KHÔNG có dòng "=== THOÁT SẠCH ===" thì biết chắc phiên trước bị kết
        // thúc đột ngột (silent exit) chứ không phải người dùng tự thoát.
        DiagnosticLogService.Trace("================ APP KHỞI ĐỘNG ================");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                DiagnosticLogService.Trace($"!!! AppDomain UNHANDLED: {ex}");
                CrashReportService.RecordCrash(ex, "AppDomain");
            }
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            if (e.Exception != null)
            {
                DiagnosticLogService.Trace($"!!! TaskScheduler UNOBSERVED: {e.Exception}");
                CrashReportService.RecordCrash(e.Exception, "TaskScheduler");
            }
            e.SetObserved();
        };
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
        {
            if (e.Exception != null)
            {
                DiagnosticLogService.Trace($"!!! WinUI UNHANDLED: {e.Exception}");
                CrashReportService.RecordCrash(e.Exception, "WinUI");
            }
            e.Handled = true; // Ngăn chặn crash app trên luồng XAML UI
        };
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            DiagnosticLogService.Trace("=== THOÁT SẠCH (ProcessExit) ===");
        };

        InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = Services.GetRequiredService<MainWindow>();
        DispatcherQueue = _mainWindow.DispatcherQueue;
        MainWindowHandle = WindowNative.GetWindowHandle(_mainWindow);
        _mainWindow.Activate();

        // Chuẩn bị sẵn tài khoản WARP (tự đăng ký qua wgcf nếu chưa có) ngay khi
        // app khởi động, để lúc người dùng bấm Boost không phải chờ đăng ký.
        _ = WarpAccountService.GetOrCreateAccountAsync();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<IWarpService, WarpCliService>();
        services.AddSingleton<MihomoService>();
        services.AddSingleton<PingMonitorService>();
        services.AddSingleton<ProcessService>();
        services.AddSingleton<GameProfileService>();
        services.AddSingleton<NetworkOptimizerService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<WarpAccountService>();
        services.AddSingleton<LocalizationService>();

        // ViewModels — DispatcherQueue được resolve lazily khi ViewModel đầu tiên được dùng
        services.AddSingleton<DashboardViewModel>(sp => new DashboardViewModel(
            sp.GetRequiredService<IWarpService>(),
            sp.GetRequiredService<PingMonitorService>(),
            sp.GetRequiredService<MihomoService>(),
            sp.GetRequiredService<LocalizationService>(),
            sp.GetRequiredService<GameProfileService>(),
            DispatcherQueue.GetForCurrentThread()
                ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()!
        ));
        services.AddSingleton<ProcessPickerViewModel>(sp =>
            new ProcessPickerViewModel(
                sp.GetRequiredService<ProcessService>(),
                sp.GetRequiredService<GameProfileService>()));
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<PingMonitorService>(),
                sp.GetRequiredService<LocalizationService>()));

        // Windows
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
