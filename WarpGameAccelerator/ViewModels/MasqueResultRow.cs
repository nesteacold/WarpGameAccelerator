using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.ViewModels;

/// <summary>
/// Bọc <see cref="MasqueProbeResult"/> (record đọc từ lịch sử quét, không có khái
/// niệm "đã ⭐ giữ sống") thêm trạng thái hiển thị cho nút ⭐ trong SettingsPage —
/// tách khỏi model đo lường để không phải sửa MasqueEndpointLab chỉ vì thêm UI.
/// </summary>
public sealed class MasqueResultRow
{
    public required MasqueProbeResult Result { get; init; }
    public MasqueEndpoint Endpoint => Result.Endpoint;
    public string DisplaySummary => Result.DisplaySummary;
    public string HttpMsText => Result.HttpMsText;
    public double? Ms => Result.WarmHttpMs;
    public bool IsError => !Result.Success;
    public string PillText => !Result.Success
        ? "Lỗi"
        : Result.Colos.Length > 0 ? $"colo {string.Join('/', Result.Colos)}" : "—";

    public required bool IsInRoster { get; init; }
    public required bool CanToggleRoster { get; init; }
    public string RosterButtonLabel => IsInRoster ? "★" : "☆";
    public required string RosterTooltip { get; init; }
}
