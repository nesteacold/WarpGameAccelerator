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

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { System.IO.File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] AppDomain Exception: {e.ExceptionObject}\n\n"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try { System.IO.File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Task Exception: {e.Exception}\n\n"); } catch { }
            e.SetObserved();
        };
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
        {
            try { System.IO.File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WinUI Exception: {e.Exception}\n\n"); } catch { }
            e.Handled = true; // Ngăn chặn crash app trên luồng XAML UI
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
