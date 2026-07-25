// ============================================================
// Dialogs/NodeSelectionDialog.xaml.cs — Code-behind Hộp thoại Chọn Node
// ============================================================
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Dialogs;

public sealed partial class NodeSelectionDialog : ContentDialog
{
    private List<GameNode> _allNodes = new();
    public GameNode SelectedNode { get; private set; }

    public NodeSelectionDialog()
    {
        InitializeComponent();
        SelectedNode = CloudflareNodeService.GetSelectedNode();
        LoadAndPingNodes();
    }

    private async void LoadAndPingNodes()
    {
        RefreshBtn.IsEnabled = false;
        StatusText.Text = "⏳ Đang quét độ trễ realtime tới các Node...";

        _allNodes = CloudflareNodeService.GetDefaultNodes();

        // Đánh dấu Node đang được chọn
        foreach (var node in _allNodes)
        {
            if (node.Id == SelectedNode.Id)
            {
                node.IsSelected = true;
            }
        }

        // Đo Ping song song
        await CloudflareNodeService.PingAllNodesAsync(_allNodes);

        UpdateListView(_allNodes);

        // Highlight dòng được chọn
        var selectedInList = _allNodes.FirstOrDefault(n => n.Id == SelectedNode.Id) ?? _allNodes[0];
        NodeListView.SelectedItem = selectedInList;

        StatusText.Text = "✓ Đã đo xong trễ realtime tất cả các Node.";
        RefreshBtn.IsEnabled = true;
    }

    private void UpdateListView(List<GameNode> nodes)
    {
        NodeListView.ItemsSource = null;
        NodeListView.ItemsSource = nodes;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim().ToLower();
        if (string.IsNullOrEmpty(query))
        {
            UpdateListView(_allNodes);
        }
        else
        {
            var filtered = _allNodes.Where(n =>
                n.Name.ToLower().Contains(query) ||
                n.Route.ToLower().Contains(query) ||
                n.EndpointIp.Contains(query)).ToList();
            UpdateListView(filtered);
        }
    }

    private void RefreshBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        LoadAndPingNodes();
    }

    private void NodeListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodeListView.SelectedItem is GameNode selected)
        {
            SelectedNode = selected;
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedNode != null)
        {
            await CloudflareNodeService.SaveSelectedNodeAsync(SelectedNode);
        }
    }
}
