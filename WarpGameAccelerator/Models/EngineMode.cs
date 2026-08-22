// ============================================================
// Models/EngineMode.cs — Chế độ kết nối (engine) cho Boost
// ============================================================
namespace WarpGameAccelerator.Models;

public enum EngineMode
{
    DirectWireGuard,
    WarpClientProxy,

    /// <summary>
    /// Direct Mode qua giao thức MASQUE (QUIC/HTTP-3). Từ v1.15.0 đây là chế độ
    /// MẶC ĐỊNH (tên enum giữ hậu tố "Beta" vì giá trị này đã được ghi vào settings
    /// của người dùng cũ — đổi tên sẽ làm họ mất lựa chọn đã lưu).
    ///
    /// Dùng tài khoản/key hoàn toàn RIÊNG (warp_masque_account.json), độc lập với
    /// DirectWireGuard. Hệ quả cần nhớ: license WARP+ áp cho mode này KHÔNG tự có
    /// ở mode kia và ngược lại — mỗi bên tốn một slot thiết bị riêng.
    /// </summary>
    DirectMasqueBeta
}
