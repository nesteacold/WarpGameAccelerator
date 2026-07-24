// ============================================================
// Services/IWarpService.cs — Interface cho WARP CLI wrapper
// ============================================================
namespace WarpGameAccelerator.Services;

public interface IWarpService
{
    /// <summary>Kiểm tra warp-cli có được cài không</summary>
    Task<bool> IsInstalledAsync();

    /// <summary>Lấy trạng thái hiện tại của WARP</summary>
    Task<WarpStatus> GetStatusAsync();

    /// <summary>Kết nối WARP</summary>
    Task<bool> ConnectAsync();

    /// <summary>Ngắt kết nối WARP</summary>
    Task<bool> DisconnectAsync();

    /// <summary>Bật Split Tunnel cho process cụ thể</summary>
    Task<bool> AddSplitTunnelProcessAsync(string processName);

    /// <summary>Xóa tất cả split tunnel rules</summary>
    Task<bool> ClearSplitTunnelAsync();
}

public enum WarpStatus
{
    Unknown,
    Connected,
    Disconnected,
    Connecting
}
