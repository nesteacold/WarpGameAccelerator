// ============================================================
// Views/MasqueColoPanel.xaml.cs — "Tìm & giữ colo" (quét endpoint MASQUE +
// giữ nhiều tunnel dự phòng sống song song). Sống trong panel phụ docked
// vào MainWindow (xem MainWindow.ShowMasqueColoPanel), KHÔNG còn nhồi vào
// cột Settings hẹp — toàn bộ logic trước đây nằm ở SettingsPage.xaml.cs đã
// chuyển hẳn sang đây theo đúng nguyên bản đã duyệt (prototype HTML), chỉ
// đổi lớp UI chứa nó, không đổi hành vi.
// ============================================================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Services;
using WarpGameAccelerator.ViewModels;

namespace WarpGameAccelerator.Views;

public sealed partial class MasqueColoPanel : UserControl
{
    private CancellationTokenSource? _masqueScan;
    private readonly List<MasqueProbeResult> _masqueRows = new();
    private MasqueEndpoint? _highlightedEndpoint;
    private bool? _httpSortAscending;
    private readonly MihomoService _mihomoService;
    private readonly MasqueTunnelSweepService _tunnelSweep;
    private readonly GameProfileService _profileService;
    private CancellationTokenSource? _multiCandidateSweep;

    /// <summary>Bấm nút đóng (X) ở header — MainWindow subscribe để resize cửa sổ về lại nhỏ.</summary>
    public event EventHandler? CloseRequested;

    public MasqueColoPanel()
    {
        InitializeComponent();
        _mihomoService = App.Services.GetRequiredService<MihomoService>();
        _tunnelSweep = App.Services.GetRequiredService<MasqueTunnelSweepService>();
        _profileService = App.Services.GetRequiredService<GameProfileService>();

        UpdateMasqueSelection();
        RestoreMasqueResults();
        InitMultiCandidateUi();
        _tunnelSweep.SweepUpdated += (_, statuses) => DispatcherQueue.TryEnqueue(() => RenderMultiCandidateRows(statuses));
        Unloaded += (_, _) => { _masqueScan?.Cancel(); _multiCandidateSweep?.Cancel(); };
    }

    private void ClosePanelBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Gọi mỗi lần panel được mở lại (MainWindow.ShowMasqueColoPanel) — panel không
    /// bị huỷ giữa các lần đóng/mở (UserControl instance giữ nguyên) nhưng trạng thái Boost
    /// có thể đã đổi trong lúc panel đóng, nên phải refresh lại "đang áp dụng thật".</summary>
    public void RefreshOnOpen()
    {
        UpdateMasqueSelection();
        RefreshMultiCandidatePanel();
    }

    // ══ Multi-candidate MASQUE (thử nghiệm, mặc định tắt) ═══════════════

    private void InitMultiCandidateUi()
    {
        MultiCandidateToggle.IsOn = MultiCandidateSettings.IsEnabled();
        MultiCandidatePanel.Visibility = MultiCandidateToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        NoRosterEmptyState.Visibility = MultiCandidateToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        RefreshMultiCandidatePanel();
    }

    private void MultiCandidateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        MultiCandidateSettings.SetEnabled(MultiCandidateToggle.IsOn);
        MultiCandidatePanel.Visibility = MultiCandidateToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        NoRosterEmptyState.Visibility = MultiCandidateToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        MultiCandidateStatusText.Text = MultiCandidateToggle.IsOn
            ? "Đã bật. Áp dụng ở lần Boost tiếp theo (mode Direct MASQUE). Tắt/bật Boost để dựng các tunnel dự phòng."
            : "Đã tắt. Boost tiếp theo dùng lại 1 tunnel MASQUE đơn như trước.";
        RefreshMultiCandidatePanel();
    }

    private void RefreshMultiCandidatePanel()
    {
        var session = _mihomoService.ActiveMasqueControl;
        if (session == null)
        {
            MultiCandidateResults.ItemsSource = null;
            MultiCandidateStatusText.Text = MultiCandidateToggle.IsOn
                ? "Chưa có tunnel dự phòng nào đang chạy — Boost ở mode Direct MASQUE để dựng."
                : "";
            return;
        }
        RenderMultiCandidateRows(_tunnelSweep.LatestSnapshot());
    }

    /// <summary>
    /// Danh sách executable của TẤT CẢ profile đã biết đang chạy hay không — dùng để quyết
    /// định có cần xác nhận 2 lần trước khi đổi GAME-PATH (xem SelectGamePath_Click). KHÔNG
    /// còn dùng để KHOÁ nút — người dùng phản hồi khoá cứng bất hợp lý vì game trước sau gì
    /// cũng sẽ chạy; giờ chỉ là tín hiệu rủi ro, không phải rào cản.
    /// </summary>
    private bool IsAnyKnownGameProcessRunning()
    {
        try
        {
            var names = _profileService.All
                .SelectMany(p => p.ExecutablesJoined.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(n => n.Trim())
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(name);
                try { if (procs.Length > 0) return true; }
                finally { foreach (var p in procs) p.Dispose(); }
            }
        }
        catch { /* Không kiểm tra được thì coi như ĐANG chạy — an toàn hơn */ return true; }
        return false;
    }

    private void RenderMultiCandidateRows(IReadOnlyList<CandidateColoStatus> statuses)
    {
        var session = _mihomoService.ActiveMasqueControl;
        if (session == null) { MultiCandidateResults.ItemsSource = null; GameRunningWarning.IsOpen = false; return; }

        bool gameRunning = IsAnyKnownGameProcessRunning();
        // Cảnh báo NGAY khi vào trang, không chỉ sau khi bấm "Chọn" rồi mới biết vì
        // sao cần xác nhận 2 lần.
        GameRunningWarning.IsOpen = gameRunning;
        var byName = statuses.ToDictionary(s => s.ProxyName);
        var rows = new List<MultiCandidateRow>();
        int index = 0;
        foreach (var (proxyName, endpoint) in session.Candidates)
        {
            index++;
            byName.TryGetValue(proxyName, out var status);
            bool isCurrent = status?.IsCurrentSelection == true;
            bool isError = status != null && status.Colo == null;
            string detail = status == null
                ? "Chưa đo — bấm \"Quét colo\" bên trên."
                : status.Colo != null
                    ? (status.VerifiedAtUtc is { } t ? $"xác thực lúc {t.ToLocalTime():HH:mm:ss}" : "")
                    : "Không đo được" + (status.VerifiedAtUtc is { } t2 ? $" (lúc {t2.ToLocalTime():HH:mm:ss})" : "");
            string pillText = status == null ? "chưa đo" : status.Colo != null ? $"colo {status.Colo}" : "lỗi";

            // KHÔNG còn khoá cứng khi game đang chạy — thay bằng xác nhận 2 lần
            // (xem SelectGamePath_Click). Bấm lần 1 chỉ "vũ trang", bấm lần 2
            // trong 4s mới thực sự đổi.
            bool canSelect = !isCurrent;
            string tooltip = isCurrent
                ? "Đang là tunnel mang traffic game."
                : gameRunning
                    ? "Đang có tiến trình game chạy — bấm 2 lần để xác nhận (vẫn đổi được, nhưng chưa kiểm chứng an toàn tuyệt đối giữa lúc đang chơi)."
                    : "Chuyển tunnel này thành đường mang traffic game — không cần Boost lại.";

            rows.Add(new MultiCandidateRow
            {
                ProxyName = proxyName,
                Title = $"Tunnel {index}",
                EndpointText = endpoint.ToString(),
                DetailText = detail,
                ButtonLabel = isCurrent ? "✓ Đang dùng" : "Chọn",
                CanSelect = canSelect,
                ButtonTooltip = tooltip,
                IsCurrent = isCurrent,
                LatencyMs = status?.LatencyMs,
                IsError = isError,
                PillText = pillText
            });
        }
        // Tunnel đang mang traffic game luôn lên đầu — đỡ phải dò cả danh sách mới biết
        // colo hiện tại là gì (đây chính là điều người dùng đã bị nhầm/bỏ sót).
        MultiCandidateResults.ItemsSource = rows.OrderByDescending(r => r.IsCurrent).ToList();
    }

    private async void SweepMultiCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_multiCandidateSweep != null) return;
        if (_mihomoService.ActiveMasqueControl == null)
        {
            MultiCandidateStatusText.Text = "Chưa có tunnel dự phòng nào đang chạy — Boost ở mode Direct MASQUE trước.";
            return;
        }
        using var cts = new CancellationTokenSource();
        _multiCandidateSweep = cts;
        MultiCandidateSweepButton.IsEnabled = false;
        MultiCandidateStatusText.Text = "Đang quét colo từng tunnel dự phòng qua PROBE-PATH (không đụng traffic game)...";
        try
        {
            await _tunnelSweep.SweepAllAsync(cts.Token);
            MultiCandidateStatusText.Text = "Đã quét xong.";
        }
        catch (OperationCanceledException) { MultiCandidateStatusText.Text = "Đã dừng quét."; }
        catch (Exception ex) { MultiCandidateStatusText.Text = "Lỗi khi quét: " + ex.Message; }
        finally
        {
            _multiCandidateSweep = null;
            MultiCandidateSweepButton.IsEnabled = true;
            RefreshMultiCandidatePanel();
        }
    }

    /// <summary>Tên candidate đang "vũ trang" chờ bấm lần 2 (xác nhận đổi khi game đang
    /// chạy) — bấm lần 1 chỉ vào đây, hết 4s không bấm lại thì tự rút khỏi danh sách.
    /// Đây là thay thế cho khoá cứng cũ (đã bỏ theo phản hồi người dùng).</summary>
    private readonly HashSet<string> _armedForConfirm = new();

    private async void SelectGamePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string proxyName } btn) return;

        if (IsAnyKnownGameProcessRunning() && !_armedForConfirm.Contains(proxyName))
        {
            _armedForConfirm.Add(proxyName);
            btn.Content = "Chắc chắn? Bấm lần nữa";
            _ = ArmTimeoutAsync(proxyName);
            return;
        }
        _armedForConfirm.Remove(proxyName);

        try
        {
            bool ok = await _mihomoService.SwitchGamePathAsync(proxyName);
            if (ok)
            {
                // Cập nhật NGAY cờ "đang dùng" trong cache — không chờ sweep kế tiếp mới
                // thấy danh sách đổi (đây là lý do nút "Chọn" trông như không phản hồi).
                _tunnelSweep.MarkSelected(proxyName);
                MultiCandidateStatusText.Text = "Đã chuyển tunnel. Đăng nhập game để dùng tunnel mới.";
            }
            else
            {
                MultiCandidateStatusText.Text = "Không chuyển được — kiểm tra Boost còn đang chạy không.";
            }
        }
        catch (Exception ex) { MultiCandidateStatusText.Text = "Lỗi khi chuyển: " + ex.Message; }
        finally { RefreshMultiCandidatePanel(); }
    }

    private async Task ArmTimeoutAsync(string proxyName)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (_armedForConfirm.Remove(proxyName))
            DispatcherQueue.TryEnqueue(RefreshMultiCandidatePanel);
    }

    private void RestoreMasqueResults()
    {
        try
        {
            var history = MasqueEndpointLab.LoadResults();
            if (history == null) return;
            var selection = MasqueEndpointLab.LoadSelection();
            _masqueRows.AddRange(history.Results);
            _highlightedEndpoint = selection;
            RefreshMasqueResults();
            MasqueStatusText.Text = $"Đã khôi phục {history.Results.Count} kết quả. Lần cập nhật cuối: {history.MeasuredAt.ToLocalTime():dd/MM HH:mm}. Kết quả cũ có thể thay đổi theo mạng/thời điểm.";
        }
        catch (Exception ex) { MasqueStatusText.Text = "Không đọc được lịch sử đo: " + ex.Message; }
    }

    /// <summary>Chip đang bấm chọn để lọc danh sách quét theo colo/lỗi — rỗng nghĩa là "Tất cả".</summary>
    private readonly HashSet<string> _activeColoFilters = new();

    private sealed record ColoChip(string Key, string Label, bool IsActive);

    private void ColoChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (key == "__all") _activeColoFilters.Clear();
        else if (!_activeColoFilters.Remove(key)) _activeColoFilters.Add(key);
        RefreshMasqueResults();
    }

    private void RebuildColoChips(List<MasqueProbeResult> allRows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int errorCount = 0;
        foreach (var row in allRows)
        {
            if (!row.Success) { errorCount++; continue; }
            foreach (var colo in row.Colos)
                counts[colo] = counts.GetValueOrDefault(colo) + 1;
        }

        var chips = new List<ColoChip> { new("__all", $"Tất cả ({allRows.Count})", _activeColoFilters.Count == 0) };
        foreach (var (colo, count) in counts.OrderByDescending(kv => kv.Value))
            chips.Add(new ColoChip(colo, $"{colo} ({count})", _activeColoFilters.Contains(colo)));
        if (errorCount > 0)
            chips.Add(new ColoChip("__error", $"Lỗi ({errorCount})", _activeColoFilters.Contains("__error")));

        ColoChipsList.ItemsSource = chips;
    }

    private void ScanExtendedMasque_Click(object sender, RoutedEventArgs e)
    {
        MasqueExtendedCheck.IsChecked = true;
        _ = RunMasqueScanAsync();
    }

    private void SortMasque_Click(object sender, RoutedEventArgs e)
    {
        _httpSortAscending = _httpSortAscending != true;
        MasqueSortButton.Content = _httpSortAscending == true ? "ms ↑" : "ms ↓";
        RefreshMasqueResults();
    }

    private void RefreshMasqueResults()
    {
        if (MasqueResults == null || MasqueCountText == null) return;
        if (MasqueResults.SelectedItem is MasqueResultRow prevSelected) _highlightedEndpoint = prevSelected.Endpoint;

        RebuildColoChips(_masqueRows);

        IEnumerable<MasqueProbeResult> rows = _masqueRows;
        if (_activeColoFilters.Count > 0)
            rows = rows.Where(row => _activeColoFilters.Contains("__error") && !row.Success
                || row.Colos.Any(colo => _activeColoFilters.Contains(colo)));
        if (_httpSortAscending is { } ascending)
            rows = rows.OrderBy(row => row.WarmHttpMs == null)
                .ThenBy(row => ascending ? row.WarmHttpMs : -row.WarmHttpMs);
        var visible = rows.ToList();

        var roster = MasqueCandidateRoster.Load();
        var wrapped = visible.Select(row =>
        {
            bool inRoster = roster.Any(e => e.Address == row.Endpoint.Address && e.Port == row.Endpoint.Port);
            bool canToggle = row.Success && (inRoster || roster.Count < 4);
            return new MasqueResultRow
            {
                Result = row,
                IsInRoster = inRoster,
                CanToggleRoster = canToggle,
                RosterTooltip = inRoster
                    ? "Bỏ khỏi danh sách giữ sống"
                    : row.Success
                        ? (roster.Count < 4 ? "Giữ sống endpoint này làm ứng viên dự phòng (tối đa 4)" : "Đã đủ 4 ứng viên — bỏ bớt 1 cái để thêm cái này")
                        : "Chỉ giữ sống được endpoint đo thành công"
            };
        }).ToList();

        MasqueResults.ItemsSource = wrapped;
        MasqueResults.SelectedItem = wrapped.FirstOrDefault(row => row.Endpoint == _highlightedEndpoint);
        MasqueCountText.Text = visible.Count == 0 && _masqueRows.Count > 0
            ? $"Không có colo phù hợp · 0/{_masqueRows.Count}"
            : $"Hiển thị {visible.Count}/{_masqueRows.Count} endpoint";

        RefreshRosterStrip(roster);
    }

    private sealed record RosterChipItem(string Label, MasqueEndpoint Endpoint);

    /// <summary>Dải chip xoá-được cho các endpoint đã ⭐ giữ sống — cùng danh sách MihomoService
    /// sẽ dùng làm ứng viên GAME-PATH/PROBE-PATH ở lần Boost tiếp theo (xem MasqueCandidateRoster).</summary>
    private void RefreshRosterStrip(List<MasqueEndpoint> roster)
    {
        RosterEmptyText.Visibility = roster.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RosterList.ItemsSource = roster.Select(e => new RosterChipItem(e.ToString(), e)).ToList();
    }

    private void RemoveRosterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MasqueEndpoint endpoint }) return;
        MasqueCandidateRoster.Toggle(endpoint);
        RefreshMasqueResults();
    }

    private void ToggleRoster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MasqueResultRow row }) return;
        if (!row.CanToggleRoster && !row.IsInRoster) return;
        MasqueCandidateRoster.Toggle(row.Endpoint);
        _highlightedEndpoint = row.Endpoint;
        RefreshMasqueResults();
    }

    // Một dòng trạng thái duy nhất khi "đã lưu" khớp "đang chạy" (trường hợp bình
    // thường); tách thành cảnh báo đỏ + gợi ý hành động khi hai nguồn lệch nhau —
    // đúng ca đã gặp thực tế (lưu SIN nhưng tunnel đang chạy HKG).
    private void UpdateMasqueSelection()
    {
        string savedText;
        MasqueEndpoint? saved = null;
        try { saved = MasqueEndpointLab.LoadSelection(); savedText = saved?.ToString() ?? "Mặc định từ Cloudflare"; }
        catch (Exception ex) { savedText = "lỗi đọc lựa chọn: " + ex.Message; }

        // Nguồn sự thật: đọc thẳng từ MihomoService (được set khi ghi config.yaml),
        // KHÔNG suy ra từ file lựa chọn — 2 nguồn này từng lệch nhau trên thực tế.
        string? activeAddress = _mihomoService.ActiveEndpointAddress;
        bool boostOff = _mihomoService.ActiveEndpointDisplay == null && activeAddress == null
                        && _mihomoService.ActiveEndpointMode == null;

        MasqueSelectionText.Text = "Lựa chọn đã lưu: " + savedText;
        MasqueActiveText.Text = "Đang áp dụng thật: " + (_mihomoService.ActiveEndpointDisplay ?? "Boost đang tắt / không phải chế độ Direct MASQUE");

        Windows.UI.Color ok = Microsoft.UI.ColorHelper.FromArgb(255, 120, 220, 140);
        Windows.UI.Color bad = Microsoft.UI.ColorHelper.FromArgb(255, 255, 120, 120);
        Windows.UI.Color neutral = Microsoft.UI.ColorHelper.FromArgb(255, 150, 150, 150);

        if (boostOff)
        {
            MasqueStatusDot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(neutral);
            MasqueSummaryText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            MasqueSummaryText.Text = "Boost đang tắt / không phải chế độ Direct MASQUE. Lựa chọn đã lưu: " + savedText;
        }
        else if (_mihomoService.ActiveEndpointMode != null && activeAddress == null)
        {
            MasqueStatusDot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(neutral);
            MasqueSummaryText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            MasqueSummaryText.Text = "Đang chạy: " + _mihomoService.ActiveEndpointMode + " (không áp dụng lựa chọn MASQUE bên dưới)";
        }
        else if (saved != null && activeAddress == saved.ToString())
        {
            MasqueStatusDot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ok);
            MasqueSummaryText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ok);
            MasqueSummaryText.Text = $"Đang chạy: {activeAddress} — khớp với lựa chọn đã lưu";
        }
        else if (saved == null && activeAddress != null)
        {
            MasqueStatusDot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ok);
            MasqueSummaryText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ok);
            MasqueSummaryText.Text = $"Đang chạy: {activeAddress} (mặc định từ Cloudflare)";
        }
        else
        {
            MasqueStatusDot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bad);
            MasqueSummaryText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(bad);
            MasqueSummaryText.Text = $"Lệch: đã lưu {savedText} nhưng đang chạy {activeAddress ?? "?"}. "
                + "Có thể do chưa Boost lại sau khi đổi endpoint — mở \"Đo & chọn endpoint khác\" bên dưới để chọn lại.";
        }
    }

    private async void ScanMasque_Click(object sender, RoutedEventArgs e) => await RunMasqueScanAsync();

    private async void RetestMasque_Click(object sender, RoutedEventArgs e)
    {
        if (MasqueResults.SelectedItem is not MasqueResultRow row)
        {
            MasqueStatusText.Text = "Chọn endpoint cần kiểm tra lại.";
            return;
        }
        await RunMasqueScanAsync(row.Endpoint);
    }

    private async Task RunMasqueScanAsync(MasqueEndpoint? only = null)
    {
        if (_masqueScan != null) return;
        using var cancellation = new CancellationTokenSource();
        _masqueScan = cancellation;
        MasqueScanButton.IsEnabled = MasqueApplyButton.IsEnabled = MasqueResetButton.IsEnabled = MasqueRetestButton.IsEnabled = MasqueExtendedCheck.IsEnabled = false;
        MasqueCancelButton.IsEnabled = true;
        int measured = 0;
        MasqueStatusText.Text = "Đang đo; kết quả được lưu sau mỗi endpoint. Đo mở rộng có thể mất 10–15 phút.";
        try
        {
            await MasqueEndpointLab.ScanAsync(result =>
            {
                int index = _masqueRows.FindIndex(row => row.Endpoint == result.Endpoint);
                if (index >= 0) _masqueRows[index] = result;
                else _masqueRows.Add(result);
                RefreshMasqueResults();
                MasqueStatusText.Text = $"Đã đo và lưu {++measured} endpoint trong lượt này. Đang tiếp tục…";
            }, cancellation.Token, MasqueExtendedCheck.IsChecked == true, only);
            MasqueStatusText.Text = "Đã đo xong. Chọn dòng thành công để thử với game. Báo cáo lưu trong Data/masque-endpoint-results.json.";
        }
        catch (OperationCanceledException) { MasqueStatusText.Text = "Đã dừng đo; các kết quả đã đo vẫn được giữ."; }
        catch (Exception ex) { MasqueStatusText.Text = ex.Message; }
        finally
        {
            _masqueScan = null;
            MasqueScanButton.IsEnabled = MasqueApplyButton.IsEnabled = MasqueResetButton.IsEnabled = MasqueRetestButton.IsEnabled = MasqueExtendedCheck.IsEnabled = true;
            MasqueCancelButton.IsEnabled = false;
        }
    }

    private void CancelMasque_Click(object sender, RoutedEventArgs e) => _masqueScan?.Cancel();

    private void ApplyMasque_Click(object sender, RoutedEventArgs e)
    {
        if (MasqueResults.SelectedItem is not MasqueResultRow { Result.Success: true } result)
        {
            MasqueStatusText.Text = "Hãy chọn một endpoint đo thành công.";
            return;
        }
        try
        {
            MasqueEndpointLab.SaveSelection(result.Endpoint);
            UpdateMasqueSelection();
            MasqueStatusText.Text = "Đã lưu. Chọn chế độ Direct MASQUE, rồi tắt/bật Boost để áp dụng. Đổi đường có thể cần đăng nhập lại game.";
        }
        catch (Exception ex) { MasqueStatusText.Text = ex.Message; }
    }

    private void ResetMasque_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MasqueEndpointLab.SaveSelection(null);
            UpdateMasqueSelection();
            MasqueStatusText.Text = "Đã khôi phục endpoint mặc định; áp dụng ở lần bật Boost tiếp theo.";
        }
        catch (Exception ex) { MasqueStatusText.Text = ex.Message; }
    }
}
