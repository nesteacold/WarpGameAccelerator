// ============================================================
// Views/MultiProcessPickerDialog.xaml.cs — Code-behind multi-select picker
// ============================================================
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Views;

public sealed partial class MultiProcessPickerDialog : ContentDialog
{
    private readonly ProcessService _processService;
    private List<GameProcess> _allProcesses = new();
    private readonly List<GameProcess> _manualProcesses = new();

    public MultiProcessPickerDialog(ProcessService processService, IEnumerable<string> preSelected)
    {
        InitializeComponent();
        _processService = processService;

        LoadProcesses(preSelected);
    }

    private void LoadProcesses(IEnumerable<string>? preSelected = null)
    {
        var preSelectedList = (preSelected ?? _allProcesses
            .Where(p => p.IsSelected)
            .Select(p => p.ProcessName)).ToList();
        var preSelectedSet = new HashSet<string>(preSelectedList, StringComparer.OrdinalIgnoreCase);

        _allProcesses = _processService.GetRunningProcesses()
            .Select(p => new GameProcess
            {
                ProcessName = p.ProcessName,
                ExePath     = p.ExePath,
                ProcessId   = p.ProcessId,
                IsSelected  = preSelectedSet.Contains(p.ProcessName)
            })
            .ToList();

        // Process đã chọn trước đó nhưng KHÔNG đang chạy lúc mở lại dialog
        // (vd ping.exe) — giữ lại ở list "thêm tay", không thì mất luôn state.
        _manualProcesses.Clear();
        var runningNames = new HashSet<string>(_allProcesses.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
        foreach (var name in preSelectedList)
        {
            if (!runningNames.Contains(name))
                _manualProcesses.Add(new GameProcess { ProcessName = name, IsSelected = true });
        }
        ManualProcessListView.ItemsSource = _manualProcesses;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(search)
            ? _allProcesses
            : _allProcesses.Where(p => p.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        ProcessListView.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void RefreshBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var current = _allProcesses.Where(p => p.IsSelected).Select(p => p.ProcessName)
            .Concat(_manualProcesses.Where(p => p.IsSelected).Select(p => p.ProcessName));
        LoadProcesses(current);
    }

    private void AddManualBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => AddManualProcess();

    private void ManualProcessBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) AddManualProcess();
    }

    private void AddManualProcess()
    {
        var name = ManualProcessBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        if (_manualProcesses.Any(p => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            _allProcesses.Any(p => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ManualProcessBox.Text = string.Empty;
            return;
        }

        _manualProcesses.Add(new GameProcess { ProcessName = name, IsSelected = true });
        ManualProcessListView.ItemsSource = null;
        ManualProcessListView.ItemsSource = _manualProcesses;
        ManualProcessBox.Text = string.Empty;
    }

    /// <summary>Đọc sau khi ShowAsync() trả về ContentDialogResult.Primary.</summary>
    public List<string> GetSelectedProcessNames() =>
        _allProcesses.Where(p => p.IsSelected)
            .Concat(_manualProcesses.Where(p => p.IsSelected))
            .Select(p => p.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
