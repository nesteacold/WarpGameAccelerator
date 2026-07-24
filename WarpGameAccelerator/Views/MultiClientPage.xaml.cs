// ============================================================
// Views/MultiClientPage.xaml.cs — Code-behind Multi-Client Launcher
// ============================================================
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using WarpGameAccelerator.Services;
using WinRT.Interop;

namespace WarpGameAccelerator.Views;

public sealed partial class MultiClientPage : Page
{
    private int    _clientCount  = 2;
    private string _currentToken = string.Empty;
    private string _gameFolder   = string.Empty;

    public MultiClientPage()
    {
        InitializeComponent();
        LoadSavedState();
        RefreshClientList();
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
    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, ((App)Application.Current).MainWindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
            FolderPathBox.Text = folder.Path;
    }

    private void FolderPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateFolder(FolderPathBox.Text.Trim());
    }

    private void ValidateFolder(string folder)
    {
        _gameFolder = folder;
        var (valid, msg) = MultiClientService.ValidateGameFolder(folder);
        FolderStatusText.Text       = msg;
        FolderStatusText.Foreground = valid
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 100))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136));

        LaunchFirstBtn.IsEnabled = valid;
    }

    // ── Card 2: Mở client đầu ───────────────────────────────
    private async void LaunchFirstBtn_Click(object sender, RoutedEventArgs e)
    {
        LaunchFirstBtn.IsEnabled = false;
        LaunchFirstBtn.Content   = "Đang mở...";

        var (ok, msg) = await MultiClientService.LaunchFirstClientAsync(_gameFolder);

        LaunchFirstBtn.IsEnabled = true;
        LaunchFirstBtn.Content   = "▶  Mở Client Đầu Tiên";
        ShowMsg(LaunchFirstMsg, msg, !ok);
    }

    // ── Card 3: Detect Token ─────────────────────────────────
    private async void DetectTokenBtn_Click(object sender, RoutedEventArgs e)
    {
        DetectTokenBtn.IsEnabled = false;
        DetectTokenBtn.Content   = "Đang quét...";

        var (ok, token, msg) = await Task.Run(() => MultiClientService.DetectTokenAsync().Result);

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

        LaunchMoreBtn.IsEnabled = false;
        LaunchMoreBtn.Content   = "Đang mở...";

        var (launched, msg) = await MultiClientService.LaunchAdditionalClientsAsync(
            _gameFolder, _currentToken, _clientCount);

        LaunchMoreBtn.IsEnabled = true;
        LaunchMoreBtn.Content   = $"▶  Mở {_clientCount} Client";
        ShowMsg(LaunchMoreMsg, msg, launched == 0);

        // Cập nhật danh sách
        await Task.Delay(1500);
        RefreshClientList();
    }

    // ── Card 5: Danh sách client đang chạy ──────────────────
    private void RefreshClientList()
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
            MultiClientService.KillClient(pid);
            RefreshClientList();
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
        foreach (var c in MultiClientService.GetRunningClients())
            MultiClientService.KillClient(c.Pid);
        RefreshClientList();
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
