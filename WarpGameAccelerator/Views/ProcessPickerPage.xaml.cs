// ============================================================
// Views/ProcessPickerPage.xaml.cs
// ============================================================
using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.ViewModels;
using WarpGameAccelerator.Services;
using System.Runtime.InteropServices;

namespace WarpGameAccelerator.Views;

public sealed partial class ProcessPickerPage : Page
{
    public ProcessPickerViewModel ViewModel { get; }
    public LocalizationService Loc { get; }

    // Filtered view for search
    private ObservableCollection<GameProcess> _filteredProcesses = [];
    public ObservableCollection<GameProcess> FilteredProcesses => _filteredProcesses;

    public ProcessPickerPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ProcessPickerViewModel>();
        Loc       = App.Services.GetRequiredService<LocalizationService>();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.ProcessConfirmed += OnProcessConfirmed;
        ViewModel.ProfileConfirmed  += OnProfileConfirmed;
        ViewModel.BrowseRequested   += OnBrowseRequested;
        ViewModel.PropertyChanged   += OnViewModelPropertyChanged;

        _ = ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ProcessConfirmed -= OnProcessConfirmed;
        ViewModel.ProfileConfirmed  -= OnProfileConfirmed;
        ViewModel.BrowseRequested   -= OnBrowseRequested;
        ViewModel.PropertyChanged   -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.Processes)
                           or nameof(ViewModel.SearchText))
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        _filteredProcesses.Clear();
        var search = ViewModel.SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        foreach (var proc in ViewModel.Processes)
        {
            if (string.IsNullOrEmpty(search)
                || proc.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || proc.ExePath.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                _filteredProcesses.Add(proc);
            }
        }
    }

    private void OnProcessConfirmed(GameProcess process)
    {
        var dashVm = App.Services.GetRequiredService<DashboardViewModel>();
        dashVm.SetSelectedProcess(process.ProcessName);
        NavigateBack();
    }

    private void ProcessList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameProcess process)
        {
            OnProcessConfirmed(process);
        }
    }

    private void OnProfileConfirmed(GameProfile profile)
    {
        var dashVm = App.Services.GetRequiredService<DashboardViewModel>();
        dashVm.SetSelectedProfile(profile);
        NavigateBack();
    }

    private void NavigateBack()
    {
        var mainWindow = App.Services.GetRequiredService<MainWindow>();
        mainWindow.NavigateToDashboard();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    private void OnBrowseRequested()
    {
        try
        {
            var ofn = new OpenFileName();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.hwndOwner = (App.Current as App)!.MainWindowHandle;
            ofn.lpstrFilter = "Executable Files (*.exe)\0*.exe\0All Files (*.*)\0*.*\0";
            ofn.lpstrFile = new string(new char[256]);
            ofn.nMaxFile = ofn.lpstrFile.Length;
            ofn.lpstrFileTitle = new string(new char[64]);
            ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
            ofn.lpstrTitle = "Chọn file Game (.exe)";
            ofn.Flags = 0x00080000 | 0x00001000 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR

            if (GetOpenFileName(ref ofn))
            {
                ViewModel.AddManualProcess(ofn.lpstrFile);
            }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(@"C:\Temp\WarpPickerCrash.log", ex.ToString());
        }
    }
}
