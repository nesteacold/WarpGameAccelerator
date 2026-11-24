using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace WarpGameAccelerator.Services;

/// <summary>
/// Optional local-only launch session handle returned by <see cref="ILocalLaunchExtension"/>.
/// Public builds ship without any registered extension (<see cref="LocalLaunchExtension.Current"/> is null).
/// </summary>
public interface ILocalLaunchSession : IDisposable
{
    Task<string> DescribeFailureAsync();
}

/// <summary>
/// Optional local-only docked side panel descriptor created by <see cref="ILocalLaunchExtension"/>.
/// </summary>
public sealed record LocalSidePanelHandle(
    UIElement Control,
    int Width,
    Action RefreshOnOpen,
    Action ClearSelection);

/// <summary>
/// Optional developer-only extension point for custom local workflows.
/// Implementations live exclusively outside the public repository and register themselves
/// via [ModuleInitializer] in local developer builds.
/// </summary>
public interface ILocalLaunchExtension
{
    string OptionLabel { get; }

    string? NavItemTag => null;
    string? NavItemLabel => null;
    string? NavItemGlyph => null;
    Type? NavPageType => null;

    void ConfigureServices(IServiceCollection services) { }

    void OnAppLaunched(IServiceProvider services) { }

    void OnAppExiting(IServiceProvider services) { }

    LocalSidePanelHandle? CreateSidePanel(Action onCloseRequested) => null;

    Task BeforeFolderLaunchAsync(string folderPath, IProgress<string>? progress) => Task.CompletedTask;

    bool TryHandleCliArgs(string[] args);

    bool IsFolderEnabled(string folderPath);

    (bool CanEnable, bool IsEnabled, string Tooltip) EvaluateFolder(string folderPath);

    string? ClearFolderState(string folderPath);

    Task<(bool Enabled, string StatusMessage)> ToggleFolderAsync(
        string folderPath,
        bool currentlyEnabled,
        IProgress<string>? progress);

    ILocalLaunchSession BeginLaunch(string folderPath, string gamePath, string token);
}

public static class LocalLaunchExtension
{
    public static ILocalLaunchExtension? Current { get; private set; }

    public static bool IsAvailable => Current != null;

    public static void Register(ILocalLaunchExtension extension)
    {
        Current = extension;
    }
}
