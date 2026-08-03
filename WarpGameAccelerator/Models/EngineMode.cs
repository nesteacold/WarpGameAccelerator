// ============================================================
// Models/EngineMode.cs — Chế độ kết nối (engine) cho Boost
// ============================================================
namespace WarpGameAccelerator.Models;

public enum EngineMode
{
    DirectWireGuard,
    WarpClientProxy,

    /// <summary>
    /// Direct Mode qua giao thức MASQUE (QUIC/HTTP-3) — BETA, không phải lựa
    /// chọn mặc định. Dùng tài khoản/key hoàn toàn riêng, độc lập với
    /// DirectWireGuard — không ảnh hưởng gì tới nhau.
    /// </summary>
    DirectMasqueBeta
}
