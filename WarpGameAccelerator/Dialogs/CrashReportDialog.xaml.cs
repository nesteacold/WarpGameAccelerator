// ============================================================
// Dialogs/CrashReportDialog.xaml.cs — Code-behind Hộp thoại Báo Cáo Sự Cố
// ============================================================
using Microsoft.UI.Xaml.Controls;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Dialogs;

public sealed partial class CrashReportDialog : ContentDialog
{
    private readonly CrashReportInfo _info;

    public CrashReportDialog(CrashReportInfo info)
    {
        InitializeComponent();
        _info = info;

        ExceptionTitleText.Text = $"⚠️  {_info.ExceptionType}";
        TimestampText.Text      = _info.Timestamp;
        MessageText.Text        = _info.Message;
        StackTraceBox.Text      = _info.StackTrace;
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            StatusText.Text = "⏳ Đang gửi báo cáo lỗi lên GitHub...";
            IsPrimaryButtonEnabled = false;

            bool sent = await CrashReportService.SendCrashReportToGitHubAsync(_info);

            if (sent)
            {
                StatusText.Text = "✓ Cảm ơn bạn! Báo cáo lỗi đã được gửi thành công.";
            }
            else
            {
                StatusText.Text = "✓ Đã lưu vết lỗi nội bộ. Cảm ơn bạn!";
            }

            await System.Threading.Tasks.Task.Delay(1000);
        }
        catch
        {
            // Bảo vệ 100%: Nuốt ngoại lệ nếu có sự cố xảy ra
            CrashReportService.ClearPendingCrashReport();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
