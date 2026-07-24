// ============================================================
// Models/AppState.cs — Trạng thái ứng dụng
// ============================================================
namespace WarpGameAccelerator.Models;

public enum AppState
{
    Idle,
    Connecting,
    Connected,
    Disconnecting,
    Error
}
