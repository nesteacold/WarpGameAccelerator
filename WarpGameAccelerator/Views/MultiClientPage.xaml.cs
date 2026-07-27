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
        _gameFolder = folder;
        var (valid, msg) = MultiClientService.ValidateGameFolder(folder);
        FolderStatusText.Text       = msg;
        FolderStatusText.Foreground = valid
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136));

        LaunchFirstBtn.IsEnabled = valid;

        if (valid)
        {
            await MultiClientService.SaveTokenAsync(_currentToken, _gameFolder);
        }
    }

    // ── Card 2: Mở client đầu ───────────────────────────────
    private async void LaunchFirstBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LaunchFirstBtn.IsEnabled = false;
            LaunchFirstBtn.Content   = "Đang mở...";

            var (ok, msg) = await MultiClientService.LaunchFirstClientAsync(_gameFolder);

            LaunchFirstBtn.IsEnabled = true;
            LaunchFirstBtn.Content   = "▶  Mở Client Đầu Tiên";
            ShowMsg(LaunchFirstMsg, msg, !ok);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.LaunchFirstBtn_Click");
            LaunchFirstBtn.IsEnabled = true;
            LaunchFirstBtn.Content   = "▶  Mở Client Đầu Tiên";
            ShowMsg(LaunchFirstMsg, $"Lỗi: {ex.Message}", isError: true);
        }
    }

    // ── Card 3: Detect Token ─────────────────────────────────
    private async void DetectTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DetectTokenBtn.IsEnabled = false;
            DetectTokenBtn.Content   = "Đang quét...";

            var (ok, token, msg) = await MultiClientService.DetectTokenAsync();

            DetectTokenBtn.IsEnabled = true;
            DetectTokenBtn.Content   = "🔍  Detect Token từ fxgame.exe";
            ShowMsg(DetectMsg, msg, !ok);

            if (ok)
            {
                _currentToken = token;
                SetTokenStatus(hasToken: true);

                // Lưu token
                await MultiClientService.SaveTokenAsync(token, _gameFolder);
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordCrash(ex, "MultiClientPage.DetectTokenBtn_Click");
            DetectTokenBtn.IsEnabled = true;
            DetectTokenBtn.Content   = "🔍  Detect Token từ fxgame.exe";
            ShowMsg(DetectMsg, $"Lỗi: {ex.Message}", isError: true);
        }
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

            LaunchMoreBtn.IsEnabled = true;
        }
        else
        {
            TokenDot.Fill                  = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 149, 0));
            TokenStatusBadge.Background    = new SolidColorBrush(ColorHelper.FromArgb(26, 255, 149, 0));
            TokenStatusBadge.BorderBrush   = new SolidColorBrush(ColorHelper.FromArgb(51, 255, 149, 0));
            TokenStatusText.Text           = "Chưa có token — mở client đầu tiên trước";
            TokenPreviewText.Text          = "Token: —";
            LaunchMoreBtn.IsEnabled        = false;
        }
    }

    // ── Card 4: Spinner + Launch ─────────────────────────────
    private void IncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_clientCount < 10) { _clientCount++; UpdateCount(); }
    }

    private void DecBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_clientCount > 1) { _clientCount--; UpdateCount(); }
    }

    private void UpdateCount()
    {
        CountText.Text            = _clientCount.ToString();
        LaunchMoreBtn.Content     = $"▶  Mở {_clientCount} Client";
    }

    private async void LaunchMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentToken))
        {
            ShowMsg(LaunchMoreMsg, "Chưa có token. Hãy thực hiện Bước 2 trước.", isError: true);
            return;
        }

        try
        {
            // Tạm dừng timer tự động làm tươi để tránh xung đột UI thread khi đang launch
            _refreshTimer.Stop();

            LaunchMoreBtn.IsEnabled = false;
            LaunchMoreBtn.Content   = "Đang mở...";
            ShowMsg(LaunchMoreMsg, "Đang khởi chạy các client, vui lòng đợi (chờ 3s mỗi client)...", isError: false);

            int count = _clientCount;
            string folder = _gameFolder;
            string token = _currentToken;

            var (launched, msg) = await MultiClientService.LaunchAdditionalClientsAsync(folder, token, count);

            LaunchMoreBtn.IsEnabled = true;
            LaunchMoreBtn.Content   = $"▶  Mở {_clientCount} Client";
            ShowMsg(LaunchMoreMsg, msg, launched == 0);
        }
        catch (Exception ex)
        {
            ShowMsg(LaunchMoreMsg, $"Lỗi: {ex.Message}", isError: true);
            LaunchMoreBtn.IsEnabled = true;
            LaunchMoreBtn.Content   = $"▶  Mở {_clientCount} Client";
        }
        finally
        {
            _refreshTimer.Start();
            RefreshClientList();
        }
    }

    // ── Card 5: Danh sách client đang chạy ──────────────────
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
