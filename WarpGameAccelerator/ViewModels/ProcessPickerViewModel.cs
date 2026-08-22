// ============================================================
// ViewModels/ProcessPickerViewModel.cs — Chọn game process
// ============================================================
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.ViewModels;

public partial class ProcessPickerViewModel : ObservableObject
{
    private readonly ProcessService _processService;
    private readonly GameProfileService _profileService;

    [ObservableProperty] private ObservableCollection<GameProcess> _processes = [];
    [ObservableProperty] private GameProcess? _selectedProcess;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Danh sách game profiles built-in + custom để hiển thị ở tab "Game đã biết"</summary>
    public IReadOnlyList<GameProfile> GameProfiles => _profileService.All;

    /// <summary>
    /// Bắn PropertyChanged cho <see cref="GameProfiles"/>. Cần gọi sau khi thêm/xoá
    /// profile: property này trả về IReadOnlyList thường (không phải
    /// ObservableCollection) nên x:Bind KHÔNG tự biết là danh sách đã đổi.
    /// </summary>
    public void NotifyProfilesChanged() => OnPropertyChanged(nameof(GameProfiles));

    public event Action<GameProcess>? ProcessConfirmed;
    public event Action<GameProfile>? ProfileConfirmed;
    public event Action? BrowseRequested;

    public ProcessPickerViewModel(ProcessService processService, GameProfileService profileService)
    {
        _processService = processService;
        _profileService = profileService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var procs = await Task.Run(() => _processService.GetRunningProcesses());
            var collection = new ObservableCollection<GameProcess>();
            foreach (var p in procs) collection.Add(p);
            Processes = collection; // Thay thế toàn bộ để kích hoạt PropertyChanged
        }
        catch { }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ConfirmSelection()
    {
        var target = SelectedProcess ?? Processes.FirstOrDefault();
        if (target is not null)
            ProcessConfirmed?.Invoke(target);
    }

    /// <summary>Người dùng bấm chọn thẳng một Game Profile từ danh sách built-in</summary>
    [RelayCommand]
    private void SelectProfile(GameProfile profile)
    {
        ProfileConfirmed?.Invoke(profile);
    }

    [RelayCommand]
    private async Task BrowseExeAsync()
    {
        // FileOpenPicker — gọi từ code-behind do cần WindowHandle
        // ViewModel raises event, View handles picker
        BrowseRequested?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Được gọi sau khi người dùng Browse chọn 1 file exe.
    /// Tự động nhận diện xem file đó thuộc Game Profile nào.
    /// Nếu có → raise ProfileConfirmed (auto-select).
    /// Nếu không → tạo ad-hoc profile và raise ProcessConfirmed.
    /// </summary>
    public void AddManualProcess(string exePath)
    {
        var fileName = Path.GetFileName(exePath);

        // Auto-detect: kiểm tra xem exe này có thuộc profile nào không
        var matchedProfile = _profileService.FindByExe(fileName);
        if (matchedProfile != null)
        {
            ProfileConfirmed?.Invoke(matchedProfile);
            return;
        }

        // Không tìm thấy profile → dùng file đơn lẻ như cũ
        var proc = new GameProcess
        {
            ProcessName = Path.GetFileNameWithoutExtension(exePath),
            ExePath     = exePath,
            IsSelected  = true
        };
        Processes.Insert(0, proc);
        SelectedProcess = proc;
        ProcessConfirmed?.Invoke(proc);
    }

    partial void OnSearchTextChanged(string value)
    {
        // Filter được thực hiện bởi CollectionViewSource trong XAML
        // Hoặc dùng ICollectionView nếu cần filter phức tạp hơn
    }
}
