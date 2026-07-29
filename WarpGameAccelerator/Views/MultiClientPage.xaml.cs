// ============================================================
// Views/MultiClientPage.xaml.cs — Code-behind Multi-Client Launcher
// ============================================================
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Views;

public sealed partial class MultiClientPage : Page
{
    private int    _clientCount  = 2;
    private string _currentToken = string.Empty;
    private string _gameFolder   = string.Empty;

    private readonly DispatcherTimer _refreshTimer;

    public MultiClientPage()
    {
        InitializeComponent();
        LoadSavedState();
        RefreshClientList();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshClientList();
        _refreshTimer.Start();

        Unloaded += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>
    /// Chờ tới khi lấy được token từ fxgame.exe đang chạy — thử mỗi 2s trong
    /// tối đa 60s để người dùng có thời gian đăng nhập trong launcher.
    /// Trả về true nếu lấy được.
    /// </summary>
    /// <summary>
    /// Chờ client đầu tiên thật sự kết nối được vào server game (đọc bảng TCP
    /// theo PID) rồi mới mở tiếp — chính xác hơn và thường nhanh hơn nhiều so
    /// với chờ cứng một số giây.
    /// </summary>
    private async Task WaitForFirstClientReadyAsync()
    {
        StartBtn.Content = "Chờ client đầu vào game...";

        var reporter = new Progress<string>(text => SetProgress(text));
        await MultiClientService.WaitForAnyClientConnectedAsync(reporter);
    }

    private async Task<bool> WaitForTokenAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);

            var (ok, token, _) = await MultiClientService.DetectTokenAsync();
            if (ok && !string.IsNullOrEmpty(token))
            {
                _currentToken = token;
                SetTokenStatus(hasToken: true);
                await MultiClientService.SaveTokenAsync(token, _gameFolder);
                DiagnosticLogService.Trace($"  lấy được token sau {(i + 1) * 2}s");
                return true;
            }

            SetProgress($"Đang chờ bạn đăng nhập... ({(i + 1) * 2}s)");
        }

        DiagnosticLogService.Trace("  TIMEOUT 60s — không lấy được token");
        return false;
    }

    // ── Khởi động: nạp token + folder đã lưu ────────────────
    private void LoadSavedState()
    {
        var info = MultiClientService.LoadToken();
        if (info == null) return;

        _currentToken = info.Token;
        _gameFolder   = info.GameFolder;

        if (!string.IsNullOrEmpty(_gameFolder))
            FolderPathBox.Text = _gameFolder;

        if (!string.IsNullOrEmpty(_currentToken))
        {
            SetTokenStatus(hasToken: true);
            ValidateFolder(_gameFolder);
        }
    }

    // ── Card 1: Browse thư mục game ─────────────────────────
    // ── Win32 Folder Browser Dialog (hoạt động trong unpackaged app) ──
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner      = ((App)Application.Current).MainWindowHandle,
                lpszTitle      = "Chọn thư mục cài đặt Age of Wushu",
                ulFlags        = 0x0001 | 0x0040, // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
                pszDisplayName = new string('\0', 260)
            };

            IntPtr pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return;

            var sb = new System.Text.StringBuilder(260);
            if (SHGetPathFromIDList(pidl, sb))
                FolderPathBox.Text = sb.ToString();

            CoTaskMemFree(pidl);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.BrowseBtn_Click");
        }
    }

    private void FolderPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateFolder(FolderPathBox.Text.Trim());
    }

    private async void ValidateFolder(string folder)
    {
        // async void: mọi exception thoát ra khỏi đây sẽ giết process ngay
        // lập tức mà không handler nào bắt được → bắt buộc bọc try/catch.
        try
        {
            _gameFolder = folder;
            var (valid, msg) = MultiClientService.ValidateGameFolder(folder);
            FolderStatusText.Text       = msg;
            FolderStatusText.Foreground = valid
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136));

            StartBtn.IsEnabled = valid;

            if (valid)
            {
                await MultiClientService.SaveTokenAsync(_currentToken, _gameFolder);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"ValidateFolder EXCEPTION: {ex}");
            CrashReportService.RecordCrash(ex, "MultiClientPage.ValidateFolder");
        }
    }

    /// <summary>
    /// MỘT nút duy nhất lo trọn quy trình: mở launcher (nếu chưa có client nào
    /// chạy) → chờ người dùng đăng nhập & tự dò token → mở nốt cho đủ tổng số
    /// cửa sổ mong muốn.
    /// </summary>
    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int target = _clientCount;
            DiagnosticLogService.Trace($"StartBtn_Click — mục tiêu tổng {target} cửa sổ");

            _refreshTimer.Stop();
            StartBtn.IsEnabled = false;

            // ── Giai đoạn 1: đảm bảo đã có client đầu tiên + token ──
            if (MultiClientService.CountRunningClients() == 0 || string.IsNullOrEmpty(_currentToken))
            {
                SetProgress("Đang mở launcher, hãy đăng nhập vào game...");
                StartBtn.Content = "Đang mở launcher...";

                var (launcherOk, launcherMsg) = await MultiClientService.LaunchFirstClientAsync(_gameFolder);
                if (!launcherOk)
                {
                    ShowMsg(StatusMsg, launcherMsg, isError: true);
                    return;
                }

                StartBtn.Content = "Đang chờ đăng nhập...";
                if (!await WaitForTokenAsync())
                {
                    SetProgress("Chưa lấy được token — hãy đăng nhập rồi bấm MỞ lại");
                    ShowMsg(StatusMsg,
                        "Hết thời gian chờ đăng nhập (60s). Sau khi vào game xong, bấm MỞ lại để mở các cửa sổ còn lại.",
                        isError: true);
                    return;
                }

                // Token xuất hiện ngay khi fxgame.exe vừa khởi động, tức là
                // client đầu MỚI BẮT ĐẦU đăng nhập chứ chưa vào game xong.
                // Mở client thứ hai ngay lúc này sẽ có hai client cùng xác
                // thực một token → server đá một cái ra ("Mạng đứt kết nối").
                // Luồng thủ công cũ vô tình tránh được vì người dùng phải bấm
                // tay 3 bước, tạo khoảng nghỉ đủ dài.
                if (target > MultiClientService.CountRunningClients())
                {
                    await WaitForFirstClientReadyAsync();
                }
            }

            // ── Giai đoạn 2: mở nốt cho đủ tổng ──
            StartBtn.Content = "Đang mở các cửa sổ...";
            SetProgress("Đang mở, chờ xác nhận từng cửa sổ trước khi mở tiếp...");

            var reporter = new Progress<string>(text =>
            {
                SetProgress(text);
                StartBtn.Content = text.Length > 40 ? "Đang mở..." : text;
            });

            var (launched, msg) = await MultiClientService.LaunchClientsToTotalAsync(
                _gameFolder, _currentToken, target, reporter);

            SetTokenStatus(hasToken: true);
            ShowMsg(StatusMsg, msg, isError: false);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Trace($"StartBtn_Click EXCEPTION: {ex}");
            CrashReportService.RecordCrash(ex, "MultiClientPage.StartBtn_Click");
            ShowMsg(StatusMsg, $"Lỗi: {ex.Message}", isError: true);
        }
        finally
        {
            StartBtn.IsEnabled = true;
            UpdateCount();
            _refreshTimer.Start();
            RefreshClientList();
            DiagnosticLogService.Trace("StartBtn_Click hoàn tất");
        }
    }

    /// <summary>Cập nhật dòng trạng thái tiến trình (badge màu cam/xanh)</summary>
    private void SetProgress(string text)
    {
        TokenDot.Fill                = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 149, 0));
        TokenStatusBadge.Background  = new SolidColorBrush(ColorHelper.FromArgb(26, 255, 149, 0));
        TokenStatusBadge.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(51, 255, 149, 0));
        TokenStatusText.Text         = text;
    }

    private void SetTokenStatus(bool hasToken)
    {
        if (hasToken)
        {
            TokenDot.Fill                  = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100));
            TokenStatusBadge.Background    = new SolidColorBrush(ColorHelper.FromArgb(26, 0, 200, 100));
            TokenStatusBadge.BorderBrush   = new SolidColorBrush(ColorHelper.FromArgb(51, 0, 200, 100));
            TokenStatusText.Text           = "Token sẵn sàng ✅";

            // Hiển thị token ẩn bớt
            var preview = _currentToken.Length > 20
                ? _currentToken[..12] + "•••" + _currentToken[^8..]
                : _currentToken;
            TokenPreviewText.Text = $"Token: {preview}";
        }
        else
        {
            SetProgress("Sẵn sàng — chọn số cửa sổ rồi bấm MỞ");
            TokenPreviewText.Text = "Token: —";
        }
    }

    // ── Spinner chọn tổng số cửa sổ ──────────────────────────
    private void IncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_clientCount < 30) { _clientCount++; UpdateCount(); }
    }

    private void DecBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_clientCount > 1) { _clientCount--; UpdateCount(); }
    }

    private void UpdateCount()
    {
        CountText.Text   = _clientCount.ToString();
        StartBtn.Content = $"▶  MỞ {_clientCount} CỬA SỔ";
    }

    // ── Card 3: Danh sách client đang chạy ──────────────────
    private void RefreshClientList()
    {
        try
        {
            var clients = MultiClientService.GetRunningClients();
            ClientListPanel.Children.Clear();

            if (clients.Count == 0)
            {
                EmptyState.Visibility      = Visibility.Visible;
                ClientListPanel.Visibility = Visibility.Collapsed;
                KillAllBtn.Visibility      = Visibility.Collapsed;
                RunningHeader.Text         = "📋  Client đang chạy (0)";
                return;
            }

            EmptyState.Visibility      = Visibility.Collapsed;
            ClientListPanel.Visibility = Visibility.Visible;
            KillAllBtn.Visibility      = Visibility.Visible;
            RunningHeader.Text         = $"📋  Client đang chạy ({clients.Count})";

            foreach (var c in clients)
            {
                var row = BuildClientRow(c.Pid, c.StartTime);
                ClientListPanel.Children.Add(row);
            }
        }
        catch
        {
            // Bảo vệ UI Thread khỏi mọi ngoại lệ khi quét process
        }
    }

    private UIElement BuildClientRow(int pid, string startTime)
    {
        var border = new Border
        {
            Background   = new SolidColorBrush(ColorHelper.FromArgb(15, 255, 255, 255)),
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(10, 8, 10, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill  = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(dot, 0);

        var info = new TextBlock
        {
            Text = $"PID {pid}  ·  {startTime}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(info, 1);

        var killBtn = new Button
        {
            Content       = "✕",
            FontSize      = 12,
            Width         = 28, Height = 28,
            CornerRadius  = new CornerRadius(4),
            Foreground    = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 100)),
            Tag           = pid
        };
        killBtn.Click += (_, _) =>
        {
            try
            {
                MultiClientService.KillClient(pid);
                RefreshClientList();
            }
            catch (Exception ex)
            {
                CrashReportService.RecordCrash(ex, "MultiClientPage.KillBtn_Click");
            }
        };
        Grid.SetColumn(killBtn, 2);

        grid.Children.Add(dot);
        grid.Children.Add(info);
        grid.Children.Add(killBtn);
        border.Child = grid;
        return border;
    }

    private void KillAllBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var c in MultiClientService.GetRunningClients())
                MultiClientService.KillClient(c.Pid);
            RefreshClientList();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.KillAllBtn_Click");
        }
    }

    // ── Helper ───────────────────────────────────────────────
    private static void ShowMsg(TextBlock tb, string msg, bool isError)
    {
        tb.Text       = msg;
        tb.Foreground = isError
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 90, 90))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100));
        tb.Visibility = Visibility.Visible;
    }
}
