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

    /// <summary>
    /// Tạo profile custom gồm NHIỀU tiến trình. Không viết dialog mới — dùng lại
    /// <see cref="MultiProcessPickerDialog"/> (vốn đã phục vụ Kênh VPN cá nhân),
    /// rồi hỏi tên và lưu qua <see cref="GameProfileService.AddCustom"/> (đã tự
    /// persist ra Data\custom_profiles.json).
    /// </summary>
    private async void NewProfileBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var processService = App.Services.GetRequiredService<ProcessService>();
            var picker = new MultiProcessPickerDialog(processService, System.Array.Empty<string>())
            {
                XamlRoot = Content.XamlRoot
            };
            if (await picker.ShowAsync() != ContentDialogResult.Primary) return;

            var selected = picker.GetSelectedProcessNames();
            if (selected.Count == 0) return;

            // Hỏi tên. Gợi ý sẵn tên tiến trình đầu để không phải gõ từ đầu.
            var nameBox = new TextBox
            {
                PlaceholderText = Loc.PickerNewProfileHint,
                Text = System.IO.Path.GetFileNameWithoutExtension(selected[0])
            };
            var nameDialog = new ContentDialog
            {
                Title             = Loc.PickerNewProfileTitle,
                Content           = nameBox,
                PrimaryButtonText = Loc.PickerBtnSelect,
                CloseButtonText   = Loc.ExitBtnCancel,
                DefaultButton     = ContentDialogButton.Primary,
                XamlRoot          = Content.XamlRoot
            };
            if (await nameDialog.ShowAsync() != ContentDialogResult.Primary) return;

            var profileName = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(profileName)) profileName = selected[0];

            var profileService = App.Services.GetRequiredService<GameProfileService>();
            var profile = new GameProfile
            {
                Name        = profileName,
                IconGlyph   = "\uE7FC",
                IsCustom    = true,
                Executables = selected
            };
            profileService.AddCustom(profile);

            // Chọn luôn profile vừa tạo rồi quay lại Dashboard — cùng luồng với
            // việc bấm vào một profile có sẵn.
            OnProfileConfirmed(profile);
        }
        catch (System.Exception ex)
        {
            // async void: KHÔNG được để exception thoát ra — sẽ kill cả process và
            // không handler nào bắt được (xem CLAUDE.md mục Process lifecycle).
            DiagnosticLogService.Trace($"[ProcessPicker] Tạo profile custom lỗi: {ex.Message}");
            CrashReportService.RecordCrash(ex, "NewProfileBtn_Click");
        }
    }

    /// <summary>
    /// Xoá profile custom. Nút này chỉ hiện với IsCustom == true (Visibility bind
    /// thẳng vào IsCustom trong DataTemplate), nên built-in không bao giờ xoá được.
    /// </summary>
    private async void DeleteProfileBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.Tag is not GameProfile profile) return;

            var confirm = new ContentDialog
            {
                Title             = Loc.PickerDeleteProfileTitle,
                Content           = $"{profile.Name}\n\n{profile.ExecutablesJoined}",
                PrimaryButtonText = Loc.PickerDeleteProfileYes,
                CloseButtonText   = Loc.ExitBtnCancel,
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = Content.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var profileService = App.Services.GetRequiredService<GameProfileService>();
            if (!profileService.RemoveCustom(profile)) return;

            // Danh sách là IReadOnlyList thường -> phải bắn PropertyChanged tay.
            ViewModel.NotifyProfilesChanged();

            // Nếu đang chọn đúng profile vừa xoá thì Dashboard sẽ trỏ vào một profile
            // không còn tồn tại. Chuyển về profile đầu tiên còn lại cho khỏi treo trạng thái.
            var dashVm = App.Services.GetRequiredService<DashboardViewModel>();
            if (dashVm.GameDisplayName == profile.Name)
            {
                var fallback = profileService.All.FirstOrDefault();
                if (fallback != null) dashVm.SetSelectedProfile(fallback);
            }
        }
        catch (System.Exception ex)
        {
            // async void: exception thoát ra sẽ kill process (xem CLAUDE.md).
            DiagnosticLogService.Trace($"[ProcessPicker] Xoá profile lỗi: {ex.Message}");
            CrashReportService.RecordCrash(ex, "DeleteProfileBtn_Click");
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
