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
            StatusText.Text = "⏳ Đang gửi báo cáo lỗi...";
            IsPrimaryButtonEnabled = false;

            bool sent = await CrashReportService.UploadPendingCrashAsync(_info);

            // Sent=false không còn có nghĩa là "đã lưu nội bộ" — trước đây nhánh này báo "✓ Đã lưu
            // vết lỗi nội bộ" đúng vào lúc file vừa bị xoá vô điều kiện.
            StatusText.Text = sent
                ? "✓ Cảm ơn bạn! Báo cáo lỗi đã được gửi."
                : "✗ Không gửi được (mất mạng hoặc chưa cấu hình kênh). Vết lỗi vẫn còn trong máy và sẽ hỏi lại ở lần mở sau.";

            await System.Threading.Tasks.Task.Delay(1000);
        }
        catch
        {
            // Bảo vệ 100%: không gửi được thì GIỮ pending, không xoá bằng chứng.
            StatusText.Text = "✗ Có lỗi khi gửi. Vết lỗi vẫn còn trong máy và sẽ hỏi lại lần sau.";
        }
        finally
        {
            deferral.Complete();
        }
    }
}
