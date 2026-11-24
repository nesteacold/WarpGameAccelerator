// ============================================================
// ViewModels/MultiClientViewModel.cs — Logic Multi-Client Launcher
// (nhiều thư mục game, mỗi thư mục là 1 hàng độc lập)
// ============================================================
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
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
    [ObservableProperty] private bool _localOptionEnabled;
    [ObservableProperty] private bool _canToggleLocalOption;
    [ObservableProperty] private string _localOptionTooltip = string.Empty;
    [ObservableProperty] private bool _isUpdatingLocalOption;

    public bool HasLocalOption => LocalLaunchExtension.IsAvailable;
    public string LocalOptionLabel => LocalLaunchExtension.Current?.OptionLabel ?? string.Empty;
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

    /// <summary>
    /// Tooltip cho chấm trạng thái — "Token" thực chất là argument khởi động fxgame.exe do
    /// fxlaunch.exe sinh ra (đổi theo phiên đăng nhập). Chỉ hiện độ dài + sha256[:8], không hiện
    /// bất kỳ đoạn nào của giá trị (bản cũ hiện 12 ký tự đầu + 8 ký tự cuối).
    /// </summary>
    public string TokenTooltip => HasToken
        ? $"Đã lưu argument khởi động cho thư mục này ({LaunchArgumentLogic.Describe(Token)})"
        : "Chưa có argument khởi động — bấm MỞ GAME để đăng nhập qua launcher và lấy argument cho thư mục này";

    /// <summary>
    /// Watermark tự cập nhật: StartTime của client NGUỒN đã sinh/xác nhận argument đang lưu. Tự cập
    /// nhật chỉ nhận client khởi động SAU mốc này — KHÔNG dùng thời điểm ghi (Codex v1 P2#6). LƯU vào
    /// game_folders.json (GameFolderEntry.ArgumentSourceStart) để sống qua lần khởi động lại Booster
    /// (Codex v2 P1#2). null = không biết (file cũ / nguồn không rõ): khi đó tự cập nhật KHÔNG ghi đè
    /// nếu còn client nào chạy bằng đúng argument đang lưu — xem LaunchArgumentLogic.PickArgumentUpdate.
    /// </summary>
    public DateTime? ArgumentSourceStart { get; set; }

    /// <summary>"đang chạy/tổng cấu hình" — gộp sẵn thành chuỗi để bind an toàn qua x:Bind.</summary>
    public string BadgeText => $"{RunningCount}/{ClientCount} cửa sổ";

    /// <summary>Chuỗi hiển thị của ô nhập số cửa sổ (TextBox chỉ bind string qua x:Bind).</summary>
    public string ClientCountText => ClientCount.ToString();

    public GameFolderRowViewModel(string path, int clientCount, DateTimeOffset addedAt, string token = "",
        DateTime? argumentSourceStart = null)
    {
        Path = path;
        ClientCount = clientCount <= 0 ? 1 : clientCount;
        AddedAt = addedAt;
        Token = token;
        LocalOptionEnabled = LocalLaunchExtension.Current?.IsFolderEnabled(path) == true;
        ArgumentSourceStart = argumentSourceStart;
        RefreshLocalOptionState();

        // Đọc version ở LUỒNG NỀN, không chặn UI: TryLoadVersion gọi
        // FindGameExe, hàm này từng quét ĐỆ QUY TOÀN BỘ cây thư mục
        // (Directory.GetFiles với AllDirectories — nay đã giới hạn 2 cấp).
        // Với một thư mục trong danh sách mà không chứa fxlaunch.exe ở
        // gốc/bin64 (vd "D:\Games" bị lượt tự quét thêm vào), nhánh đó quét
        // cạn cả ổ đĩa — hàng trăm nghìn file, mất nhiều giây. Constructor này
        // chạy trong LoadState() TRÊN UI THREAD lúc mở trang multi-launcher, và
        // bị gọi 2 lần cho MỖI thư mục (fxlaunch rồi fxgame), nên app đóng băng
        // ngay khi bấm vào multi-launcher. Version chỉ là nhãn hiển thị phụ,
        // không cần có ngay — đọc xong mới marshal về UI thread để gán.
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            var version = ReadVersionOrEmpty();
            if (string.IsNullOrEmpty(version)) return;
            if (dispatcher != null) dispatcher.TryEnqueue(() => VersionText = version);
            else VersionText = version;
        });
    }

    public void RefreshLocalOptionState()
    {
        var ext = LocalLaunchExtension.Current;
        if (ext == null) return;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            var (canEnable, isEnabled, tip) = ext.EvaluateFolder(Path);
            void Apply()
            {
                LocalOptionEnabled = isEnabled;
                CanToggleLocalOption = canEnable && !IsUpdatingLocalOption;
                LocalOptionTooltip = tip;
            }
            if (dispatcher != null) dispatcher.TryEnqueue(Apply); else Apply();
        });
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
    /// bịa số nếu đọc lỗi/rỗng — trả chuỗi rỗng (xem CLAUDE.md mục "Chỉ số
    /// hiển thị: KHÔNG được bịa"). Chỉ ĐỌC, không gán property — caller chạy
    /// hàm này ở luồng nền rồi mới marshal kết quả về UI thread.
    /// </summary>
    private string ReadVersionOrEmpty()
    {
        try
        {
            var exe = MultiClientService.FindGameExe(Path, "fxlaunch.exe")
                      ?? MultiClientService.FindGameExe(Path, "fxgame.exe");
            if (exe == null) return string.Empty;

            var info = FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? string.Empty : info.FileVersion!;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public partial class MultiClientViewModel : ObservableObject
{
    private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HiddenRefreshInterval  = TimeSpan.FromSeconds(15);
    private static MultiClientViewModel? _instance;
    private static volatile bool _hostWindowHidden;

    // Windows tự tạo lại/hiện lại cửa sổ hệ thống (Default IME...) theo mỗi
    // lần đổi focus bàn phím — độc lập với ShowWindow(SW_HIDE) ban đầu. Phải
    // nhớ PID nào người dùng CHỦ Ý ẩn rồi re-assert mỗi tick refresh.
    private readonly HashSet<int> _hiddenPids = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherQueue? _dispatcher;
    private volatile bool _refreshInFlight;
    private bool _refreshQueued;

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
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _instance = this;

        LoadState();
        RefreshRunning();

        _refreshTimer = new DispatcherTimer
        {
            Interval = _hostWindowHidden ? HiddenRefreshInterval : VisibleRefreshInterval
        };
        _refreshTimer.Tick += (_, _) => RefreshRunning();
        _refreshTimer.Start();
    }

    public static void NotifyWindowHidden(bool hidden)
    {
        _hostWindowHidden = hidden;
        var inst = _instance;
        if (inst?._dispatcher == null) return;
        inst._dispatcher.TryEnqueue(() =>
        {
            inst._refreshTimer.Interval = hidden ? HiddenRefreshInterval : VisibleRefreshInterval;
            if (!hidden) inst.RefreshRunning();
        });
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
                Folders.Add(new GameFolderRowViewModel(e.Path, e.LastClientCount, e.AddedAt, e.Token,
                    e.ArgumentSourceStart));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.LoadState");
        }
    }

    private void PersistFolders()
    {
        var entries = Folders.Select(f => new GameFolderEntry(f.Path, f.ClientCount, f.AddedAt, f.Token,
            f.ArgumentSourceStart)).ToList();
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
            if (LocalLaunchExtension.Current?.ClearFolderState(row.Path) is { } err)
                DiagnosticLogService.Trace("  " + err);
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

    public async Task ToggleLocalOptionAsync(GameFolderRowViewModel row)
    {
        var ext = LocalLaunchExtension.Current;
        if (ext == null || row == null || row.IsUpdatingLocalOption) return;
        bool done = false;
        try
        {
            row.IsUpdatingLocalOption = true;
            row.CanToggleLocalOption = false;
            var progress = new Progress<string>(line =>
            {
                if (!done) row.StatusText = line;
            });
            var (enabled, msg) = await ext.ToggleFolderAsync(row.Path, row.LocalOptionEnabled, progress);
            done = true;
            row.LocalOptionEnabled = enabled;
            if (!string.IsNullOrEmpty(msg)) row.StatusText = msg;
        }
        catch (Exception ex)
        {
            done = true;
            row.StatusText = $"Lỗi ({ex.GetType().Name})";
            CrashReportService.RecordCrash(ex, "MultiClientViewModel.ToggleLocalOptionAsync");
        }
        finally
        {
            row.IsUpdatingLocalOption = false;
            row.RefreshLocalOptionState();
        }
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

            // BẮT BUỘC Task.Run: method async chạy ĐỒNG BỘ trên thread của
            // caller cho tới await đầu tiên thực sự nhường quyền.
            // EnsureClientsRunningAsync mở màn bằng LaunchGate.WaitAsync()
            // (hoàn tất đồng bộ khi gate rảnh) rồi gọi ngay
            // CountRunningFxgameInFolder — truy vấn WMI đồng bộ tốn hàng trăm
            // ms tới nhiều giây (vô hạn nếu dịch vụ WMI treo). Không có
            // Task.Run thì toàn bộ đoạn đó chạy TRÊN UI THREAD ngay khi bấm
            // MỞ GAME → app đóng băng (đã xảy ra thật, nhiều lần). Progress<T>
            // tạo ở trên (UI thread) nên các report từ thread pool vẫn tự
            // marshal về UI đúng cách.
            var result = await Task.Run(() => MultiClientService.EnsureClientsRunningAsync(
                row.Path, row.Token, row.ClientCount, progress, row.LocalOptionEnabled));
            var msg = result.Message;
            var token = result.Token;

            bool changed = !string.IsNullOrEmpty(token) && token != row.Token;
            if (changed) row.Token = token;
            row.ArgumentSourceStart = LaunchArgumentLogic.NextWatermark(
                row.ArgumentSourceStart, result.ArgumentSourceStart, changed);

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

    // Chiều ẩn/hiện lấy từ _hiddenPids (trạng thái CHỦ Ý) chứ không từ
    // RunningClient.IsVisible: show/hide chạy nền nên trạng thái thật có thể
    // còn chưa kịp đổi khi người dùng bấm tiếp. _hiddenPids chỉ tồn tại trong
    // bộ nhớ nên khi app khởi động lại nó rỗng — vẫn hỏi thẳng Windows như cũ.
    public void ToggleHideAll(GameFolderRowViewModel row)
    {
        try
        {
            bool anyVisible = row.RunningClients.Any(c => !_hiddenPids.Contains(c.Pid));
            foreach (var c in row.RunningClients)
            {
                if (anyVisible) { _hiddenPids.Add(c.Pid); WindowHelper.RequestHide(c.Pid); }
                else { _hiddenPids.Remove(c.Pid); WindowHelper.RequestUnhide(c.Pid); }
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
            if (!_hiddenPids.Contains(client.Pid)) { _hiddenPids.Add(client.Pid); WindowHelper.RequestHide(client.Pid); }
            else { _hiddenPids.Remove(client.Pid); WindowHelper.RequestUnhide(client.Pid); }
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

    // ── Làm mới danh sách client đang chạy (mỗi 2s khi hiện / 15s khi ẩn tray) ──
    // Đẩy GetRunningClients (NtQuerySystemInformation + QueryFullProcessImageName + EnumWindows)
    // sang Task.Run để không chặn UI thread khi máy gặp đợt nghẽn tải hệ thống (đã thấy trực tiếp
    // trên stack UI của dump 6416).
    public void RefreshRunning()
    {
        try
        {
            var dispatcher = _dispatcher ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
            {
                var knownFolders = Folders.Select(f => f.Path).ToList();
                var clients = MultiClientService.GetRunningClients(knownFolders);
                ApplyRunningClients(clients);
                return;
            }

            if (_refreshInFlight)
            {
                _refreshQueued = true;
                return;
            }

            _refreshInFlight = true;
            var snapshotFolders = Folders.Select(f => f.Path).ToList();

            _ = Task.Run(() =>
            {
                List<RunningClient>? clients = null;
                try
                {
                    clients = MultiClientService.GetRunningClients(snapshotFolders);
                }
                catch
                {
                    // Bảo vệ khỏi mọi ngoại lệ khi quét process — timer gọi lại sau.
                }

                bool posted = dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        if (clients != null)
                            ApplyRunningClients(clients);
                    }
                    catch
                    {
                        // Bảo vệ UI thread khỏi ngoại lệ khi cập nhật danh sách.
                    }
                    finally
                    {
                        _refreshInFlight = false;
                        if (_refreshQueued)
                        {
                            _refreshQueued = false;
                            RefreshRunning();
                        }
                    }
                });

                if (!posted)
                    _refreshInFlight = false;
            });
        }
        catch
        {
            _refreshInFlight = false;
        }
    }

    private void ApplyRunningClients(List<RunningClient> clients)
    {
        var runningPids = clients.Select(c => c.Pid).ToHashSet();
        foreach (var deadPid in _hiddenPids.Where(pid => !runningPids.Contains(pid)).ToList())
            WindowHelper.ForgetPid(deadPid);
        _hiddenPids.RemoveWhere(pid => !runningPids.Contains(pid));
        foreach (var pid in _hiddenPids)
            WindowHelper.RequestHide(pid);

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

        if (++_refreshTicks % ArgumentCheckEveryTicks == 0)
            StartArgumentAutoUpdate();
    }

    // ── D: tự cập nhật argument khởi động từ client đã kết nối ─────────
    // Người dùng tự mở client qua launcher (fxlaunch) thì argument mới nằm trong dòng lệnh của
    // fxgame.exe đó; nếu client ấy ỔN ĐỊNH (sống ≥ 3 phút, không bị WER treo — bất kể đi qua proxy
    // hay không) và mang argument khác argument đã lưu thì lưu đè cho đúng thư mục. 10 s một lần (5 nhịp refresh), chạy NỀN: RefreshRunning chạy trên UI
    // thread mà đọc PEB/bảng TCP của nhiều PID không thuộc ngân sách 6–7 ms/nhịp đã đo.
    private const int ArgumentCheckEveryTicks = 5;
    private int _refreshTicks;
    private bool _argumentCheckRunning;

    private void StartArgumentAutoUpdate()
    {
        if (_argumentCheckRunning) return;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher == null) return;

        // Thư mục đang MỞ GAME thì bỏ qua: EnsureClientsRunningAsync đang tự xác nhận/làm mới
        // argument của nó, ghi chéo ở đây sẽ đua với kết quả của lượt mở.
        var work = Folders
            .Where(r => !r.IsBusy && r.RunningCount > 0)
            .Select(r => (Row: r, r.Path, Saved: r.Token, NotBefore: r.ArgumentSourceStart))
            .ToList();
        if (work.Count == 0) return;

        _argumentCheckRunning = true;
        _ = Task.Run(() =>
        {
            var found = new List<(GameFolderRowViewModel Row, string Saved, FolderClientSnapshot Pick)>();
            try
            {
                foreach (var w in work)
                {
                    var pick = MultiClientService.FindUpdatedArgument(w.Path, w.Saved, w.NotBefore);
                    if (pick != null) found.Add((w.Row, w.Saved, pick));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Trace($"[argument khởi động] tự cập nhật lỗi: {ex.GetType().Name}");
            }

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    bool changed = false;
                    foreach (var f in found)
                    {
                        // Lượt mở game vừa bắt đầu, hoặc argument đã đổi bằng đường khác trong lúc
                        // chạy nền ⇒ kết quả này đã cũ, bỏ.
                        if (f.Row.IsBusy || f.Row.Token != f.Saved || !Folders.Contains(f.Row)) continue;
                        f.Row.Token = f.Pick.Argument;
                        f.Row.ArgumentSourceStart = f.Pick.StartTime;   // watermark = StartTime nguồn (P2#6)
                        f.Row.StatusText = $"Đã tự cập nhật argument khởi động từ client PID {f.Pick.Pid} (đang chạy ổn định).";
                        changed = true;
                    }
                    if (changed) PersistFolders();
                }
                catch (Exception ex)
                {
                    CrashReportService.RecordCrash(ex, "MultiClientViewModel.StartArgumentAutoUpdate");
                }
                finally
                {
                    _argumentCheckRunning = false;
                }
            });
        });
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
