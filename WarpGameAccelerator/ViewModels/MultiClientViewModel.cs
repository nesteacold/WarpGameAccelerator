// ============================================================
// ViewModels/MultiClientViewModel.cs — Logic Multi-Client Launcher
// (nhiều thư mục game, mỗi thư mục là 1 hàng độc lập)
// ============================================================
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.ViewModels;

/// <summary>
/// Một hàng trong bảng thư mục game: bọc GameFolderEntry (đường dẫn + số cửa
/// sổ đã nhớ) cộng trạng thái sống (đang chạy, đang bận, version...).
/// </summary>
public partial class GameFolderRowViewModel : ObservableObject
{
    public string Path { get; }
    public DateTimeOffset AddedAt { get; }

    // Token PER-FOLDER — mỗi thư mục là 1 tài khoản/phiên đăng nhập riêng
    // (xem GameFolderEntry.Token). Lấy được bằng cách mở fxlaunch.exe của
    // CHÍNH thư mục này lần đầu, chờ fxgame.exe chạy lên, rồi đọc token từ
    // command line của nó (MultiClientService.EnsureClientsRunningAsync).
    [ObservableProperty] private string _token = string.Empty;
    [ObservableProperty] private int _clientCount;
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    public ObservableCollection<RunningClient> RunningClients { get; } = new();

    /// <summary>Rút gọn đường dẫn dài để hiện trong bảng — tooltip vẫn hiện đầy đủ (xem XAML).</summary>
    public string ShortPath => Path.Length > 52 ? "..." + Path[^49..] : Path;

    public bool HasVersion => !string.IsNullOrEmpty(VersionText);

    public bool HasToken => !string.IsNullOrEmpty(Token);

    /// <summary>Tooltip cho chấm trạng thái token — không lộ toàn bộ token ra UI.</summary>
    public string TokenTooltip => HasToken
        ? $"Token riêng của thư mục này: {(Token.Length > 20 ? Token[..12] + "•••" + Token[^8..] : Token)}"
        : "Chưa có token — bấm MỞ GAME để đăng nhập lần đầu và lấy token cho thư mục này";

    /// <summary>"đang chạy/tổng cấu hình" — gộp sẵn thành chuỗi để bind an toàn qua x:Bind.</summary>
    public string BadgeText => $"{RunningCount}/{ClientCount} cửa sổ";

    /// <summary>Chuỗi hiển thị của ô nhập số cửa sổ (TextBox chỉ bind string qua x:Bind).</summary>
    public string ClientCountText => ClientCount.ToString();

    public GameFolderRowViewModel(string path, int clientCount, DateTimeOffset addedAt, string token = "")
    {
        Path = path;
        ClientCount = clientCount <= 0 ? 1 : clientCount;
        AddedAt = addedAt;
        Token = token;
        TryLoadVersion();
    }

    partial void OnVersionTextChanged(string value) => OnPropertyChanged(nameof(HasVersion));
    partial void OnTokenChanged(string value)
    {
        OnPropertyChanged(nameof(HasToken));
        OnPropertyChanged(nameof(TokenTooltip));
    }
    partial void OnRunningCountChanged(int value) => OnPropertyChanged(nameof(BadgeText));
    partial void OnClientCountChanged(int value)
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(ClientCountText));
    }

    /// <summary>
    /// Best-effort: đọc FileVersion của fxlaunch/fxgame trong thư mục. KHÔNG
    /// bịa số nếu đọc lỗi/rỗng — để trống hoàn toàn (xem CLAUDE.md mục "Chỉ
    /// số hiển thị: KHÔNG được bịa").
    /// </summary>
    private void TryLoadVersion()
    {
        try
        {
            var exe = MultiClientService.FindGameExe(Path, "fxlaunch.exe")
                      ?? MultiClientService.FindGameExe(Path, "fxgame.exe");
            if (exe == null) { VersionText = string.Empty; return; }

            var info = FileVersionInfo.GetVersionInfo(exe);
            VersionText = string.IsNullOrWhiteSpace(info.FileVersion) ? string.Empty : info.FileVersion!;
        }
        catch
        {
            VersionText = string.Empty;
        }
    }
}

public partial class MultiClientViewModel : ObservableObject
{
    // Windows tự tạo lại/hiện lại cửa sổ hệ thống (Default IME...) theo mỗi
    // lần đổi focus bàn phím — độc lập với ShowWindow(SW_HIDE) ban đầu. Phải
    // nhớ PID nào người dùng CHỦ Ý ẩn rồi re-assert mỗi tick refresh.
    private readonly HashSet<int> _hiddenPids = new();
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<GameFolderRowViewModel> Folders { get; } = new();

    /// <summary>Client fxgame.exe đang chạy nhưng không khớp thư mục nào đã cấu hình.</summary>
    public ObservableCollection<RunningClient> UnknownClients { get; } = new();

    [ObservableProperty] private bool _hasUnknownClients;

    // Token giờ PER-FOLDER — xem GameFolderRowViewModel.Token/HasToken.
    // Không còn khái niệm token account-level dùng chung ở tầng ViewModel này.

    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoveryStatusText = string.Empty;

    public MultiClientViewModel()
    {
        LoadState();
        RefreshRunning();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshRunning();
        _refreshTimer.Start();
    }

    public void OnNavigatedTo() => _refreshTimer.Start();
    public void OnNavigatedFrom() => _refreshTimer.Stop();

    // ── Khởi động: nạp danh sách thư mục đã lưu (token đi kèm mỗi thư mục) ──
    private void LoadState()
    {
        try
        {
            var entries = MultiClientService.LoadGameFolders();
            foreach (var e in entries)
                Folders.Add(new GameFolderRowViewModel(e.Path, e.LastClientCount, e.AddedAt, e.Token));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.LoadState");
        }
    }

    private void PersistFolders()
    {
        var entries = Folders.Select(f => new GameFolderEntry(f.Path, f.ClientCount, f.AddedAt, f.Token)).ToList();
        MultiClientService.SaveGameFolders(entries);
    }

    // ── Thêm thư mục ─────────────────────────────────────────
    [RelayCommand]
    private async Task DiscoverFoldersAsync()
    {
        if (IsDiscovering) return;
        try
        {
            IsDiscovering = true;
            DiscoveryStatusText = "Đang quét ổ đĩa (có thể mất tới 1 phút)...";

            var progress = new Progress<string>(t => DiscoveryStatusText = t);
            var found = await Task.Run(() => MultiClientService.DiscoverGameFolders(progress: progress));

            int added = 0;
            foreach (var folder in found)
            {
                if (Folders.Any(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Folders.Add(new GameFolderRowViewModel(folder, 2, DateTimeOffset.Now));
                added++;
            }

            if (added > 0) PersistFolders();

            DiscoveryStatusText = added > 0
                ? $"Quét xong — tìm thấy thêm {added} thư mục game mới."
                : "Quét xong — không tìm thấy thư mục game mới nào (đã có sẵn trong danh sách, hoặc không quét ra).";
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.DiscoverFoldersAsync");
            DiscoveryStatusText = $"Lỗi khi quét: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>Thêm thủ công (gọi từ Page sau khi người dùng chọn thư mục qua dialog Win32).</summary>
    public void AddFolder(string path)
    {
        try
        {
            var (valid, msg) = MultiClientService.ValidateGameFolder(path);
            if (!valid)
            {
                DiscoveryStatusText = msg;
                return;
            }
            if (Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                DiscoveryStatusText = "Thư mục này đã có trong danh sách.";
                return;
            }
            Folders.Add(new GameFolderRowViewModel(path, 2, DateTimeOffset.Now));
            PersistFolders();
            DiscoveryStatusText = "Đã thêm thư mục.";
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.AddFolder");
        }
    }

    public void RemoveFolder(GameFolderRowViewModel row)
    {
        try
        {
            Folders.Remove(row);
            PersistFolders();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.RemoveFolder");
        }
    }

    public void IncrementCount(GameFolderRowViewModel row)
    {
        if (row.ClientCount < 30) { row.ClientCount++; PersistFolders(); }
    }

    public void DecrementCount(GameFolderRowViewModel row)
    {
        if (row.ClientCount > 1) { row.ClientCount--; PersistFolders(); }
    }

    public void SetCount(GameFolderRowViewModel row, int value)
    {
        if (value < 1) value = 1;
        if (value > 30) value = 30;
        row.ClientCount = value;
        PersistFolders();
    }

    // ── Mở game / dừng-ẩn theo từng hàng ─────────────────────
    [RelayCommand]
    private async Task LaunchAsync(GameFolderRowViewModel row)
    {
        if (row == null || row.IsBusy) return;
        try
        {
            row.IsBusy = true;
            row.StatusText = "Đang xử lý...";

            var progress = new Progress<string>(t => row.StatusText = t);
            var (_, msg, token) = await MultiClientService.EnsureClientsRunningAsync(
                row.Path, row.Token, row.ClientCount, progress);

            if (!string.IsNullOrEmpty(token) && token != row.Token)
                row.Token = token;

            row.StatusText = msg;
            PersistFolders();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.LaunchAsync");
            row.StatusText = $"Lỗi: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            RefreshRunning();
        }
    }

    public void StopAll(GameFolderRowViewModel row)
    {
        try
        {
            foreach (var c in row.RunningClients.ToList())
                MultiClientService.KillClient(c.Pid);
            RefreshRunning();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.StopAll");
        }
    }

    public void ToggleHideAll(GameFolderRowViewModel row)
    {
        try
        {
            bool anyVisible = row.RunningClients.Any(c => c.IsVisible);
            foreach (var c in row.RunningClients)
            {
                if (anyVisible) { _hiddenPids.Add(c.Pid); WindowHelper.HideClient(c.Pid); }
                else { _hiddenPids.Remove(c.Pid); WindowHelper.UnhideClient(c.Pid); }
            }
            RefreshRunning();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.ToggleHideAll");
        }
    }

    public void ToggleHideOne(RunningClient client)
    {
        try
        {
            if (client.IsVisible) { _hiddenPids.Add(client.Pid); WindowHelper.HideClient(client.Pid); }
            else { _hiddenPids.Remove(client.Pid); WindowHelper.UnhideClient(client.Pid); }
            RefreshRunning();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.ToggleHideOne");
        }
    }

    public void KillOne(RunningClient client)
    {
        try
        {
            MultiClientService.KillClient(client.Pid);
            RefreshRunning();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.KillOne");
        }
    }

    // ── Làm mới danh sách client đang chạy (mỗi 2s) ──────────
    public void RefreshRunning()
    {
        try
        {
            var knownFolders = Folders.Select(f => f.Path).ToList();
            var clients = MultiClientService.GetRunningClients(knownFolders);

            var runningPids = clients.Select(c => c.Pid).ToHashSet();
            foreach (var deadPid in _hiddenPids.Where(pid => !runningPids.Contains(pid)).ToList())
                WindowHelper.ForgetPid(deadPid);
            _hiddenPids.RemoveWhere(pid => !runningPids.Contains(pid));
            foreach (var pid in _hiddenPids)
                WindowHelper.HideClient(pid);

            foreach (var row in Folders)
            {
                var mine = clients
                    .Where(c => c.FolderPath != null && string.Equals(c.FolderPath, row.Path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                SyncRunningCollection(row.RunningClients, mine);
                row.RunningCount = mine.Count;
            }

            var unknown = clients.Where(c => c.FolderPath == null).ToList();
            SyncRunningCollection(UnknownClients, unknown);
            HasUnknownClients = unknown.Count > 0;
        }
        catch
        {
            // Bảo vệ khỏi mọi ngoại lệ khi quét process — timer gọi lại sau 2s.
        }
    }

    private static void SyncRunningCollection(ObservableCollection<RunningClient> target, List<RunningClient> source)
    {
        // RunningClient không implement INotifyPropertyChanged nên cập nhật
        // tại chỗ sẽ không tự đẩy thay đổi (vd IsVisible) lên UI — luôn
        // rebuild để hiển thị đúng. Danh sách nhỏ (vài client), refresh mỗi
        // 2s nên chi phí Clear+Add không đáng kể.
        target.Clear();
        foreach (var c in source) target.Add(c);
    }
}
