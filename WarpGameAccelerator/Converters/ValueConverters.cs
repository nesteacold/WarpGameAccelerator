// ============================================================
// Converters/ValueConverters.cs — XAML converters
// ============================================================
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WarpGameAccelerator.Converters;

/// <summary>bool → Visibility</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>bool → Visibility (đảo ngược)</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// Ping ms → SolidColorBrush (xanh lá = tốt, vàng = trung bình, đỏ = xấu)
/// Dùng cho TextBlock Foreground
/// </summary>
public class PingColorConverter : IValueConverter
{
    // Thresholds
    private const long GoodMs     = 60;
    private const long WarningMs  = 120;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long ms = value switch
        {
            long   l => l,
            int    i => i,
            double d => (long)d,
            _        => 0
        };

        if (ms <= 0)
            return new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 136)); // grey

        if (ms <= GoodMs)
            return new SolidColorBrush(ColorHelper.FromArgb(255, 52, 211, 153));  // green

        if (ms <= WarningMs)
            return new SolidColorBrush(ColorHelper.FromArgb(255, 251, 191, 36));  // yellow

        return new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68));       // red
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Conflict detected (bool) → badge màu: đỏ/cam = đã phát hiện, xám = không.</summary>
public class DetectedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 100, 100));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Conflict detected (bool) → nhãn badge.</summary>
public class DetectedToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "ĐÃ PHÁT HIỆN" : "KHÔNG THẤY";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
