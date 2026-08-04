// ============================================================
// Views/PersonalVpnProfileItem.cs — item nhẹ cho ListView (x:Bind) hiển thị
// danh sách profile VPN cá nhân trong Dev Panel (MainWindow.xaml).
// ============================================================
namespace WarpGameAccelerator.Views;

public class PersonalVpnProfileItem
{
    public string Id       { get; set; } = string.Empty;
    public string Name     { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}
