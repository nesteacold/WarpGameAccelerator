namespace WarpGameAccelerator.ViewModels;

/// <summary>
/// Một dòng hiển thị cho ListView "nhiều tunnel dự phòng" trong SettingsPage —
/// tách khỏi CandidateColoStatus (Services) để không lẫn logic đo với logic hiển thị/
/// gating nút bấm (an toàn khi game đang chạy — xem SettingsPage.xaml.cs).
/// </summary>
public sealed class MultiCandidateRow
{
    public required string ProxyName { get; init; }
    public required string Title { get; init; }
    public required string EndpointText { get; init; }
    public required string DetailText { get; init; }
    public required string ButtonLabel { get; init; }
    public required bool CanSelect { get; init; }
    public required string ButtonTooltip { get; init; }

    /// <summary>Đang là tunnel mang traffic game — dùng để đẩy dòng này lên đầu và tô nổi bật.</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>Null = chưa đo/không đo được — pill hiển thị màu xám trung tính, KHÔNG bịa số.</summary>
    public double? LatencyMs { get; init; }
    public string LatencyText => LatencyMs is { } ms ? $"{ms:0} ms" : "";
    public required bool IsError { get; init; }
    public required string PillText { get; init; }
}
