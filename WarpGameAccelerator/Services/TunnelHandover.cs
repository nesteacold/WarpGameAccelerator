namespace WarpGameAccelerator.Services;

/// <summary>
/// FR-12/FR-13: khi mở app sau một lần treo/crash, đường hầm của phiên trước có còn không và
/// app xử lý nó thế nào — VÀ câu đó phải được nói ĐÚNG với người dùng.
///
/// Vì sao lớp này tồn tại sau khi đã có spec: spec (001, mục Open Questions) viết theo hành vi
/// CŨ — "mở lại app là kill mihomo cũ ⇒ rớt kết nối tại lúc mở lại". Code hiện tại đã khác, và
/// khác theo yêu cầu rõ ràng của chủ dự án: `DashboardViewModel` thấy mihomo còn sống thì gọi
/// `AdoptRunningTunnel(...)` (chỉ ghi nhận trạng thái, KHÔNG kill — đo: thân hàm 6 dòng gán
/// field, không có lời gọi giết nào), còn `StopProxy()` chỉ xuất hiện ở ExitApp và ở hành động
/// người dùng bấm dừng. Nên nếu để thông báo FR-13 nối theo spec cũ thì app sẽ dọa người dùng
/// bằng một thiệt hại không còn tồn tại.
///
/// Văn bản vì thế được suy từ TRẠNG THÁI, không hard-code.
/// </summary>
public static class TunnelHandover
{
    public enum Verdict
    {
        Adopted,       // tunnel phiên trước còn sống ⇒ app nhận nuôi, không cắt
        NoneRunning,   // không có tunnel (máy vừa bật lại, hoặc user đã dừng từ trước)
    }

    public static Verdict Decide(bool previousTunnelAlive) =>
        previousTunnelAlive ? Verdict.Adopted : Verdict.NoneRunning;

    /// <summary>Một dòng sự thật cho dialog FR-05. Nói rõ ai còn sống, app có cắt không,
    /// và người dùng phải chờ gì nếu bấm Start Boost lại.</summary>
    public static string StatusLine(Verdict v) => v switch
    {
        Verdict.Adopted =>
            "Kết nối game của bạn VẪN ĐANG chạy — app đã nhận lại đường hầm cũ và KHÔNG cắt nó. " +
            "Nếu bạn bấm Start Boost lại, lúc đó kết nối hiện tại mới bị thay bằng đường hầm mới.",
        Verdict.NoneRunning =>
            "Không còn kết nối nào của lần chạy trước đang chạy — mở lại app không làm rớt gì cả.",
        _ => string.Empty,
    };
}
