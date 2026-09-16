using System.Text.Json;

namespace WarpGameAccelerator.Services;

/// <summary>Hành động đã ghi nhớ khi người dùng tick "Không hỏi lại" ở hộp thoại
/// xác nhận thoát (MainWindow.PromptExitAsync).</summary>
public enum RememberedExitAction
{
    Exit,
    Minimize,
}

/// <summary>
/// Lưu lựa chọn "Không hỏi lại" của hộp thoại xác nhận thoát (X trên thanh
/// tiêu đề) — cùng idiom file JSON phẳng dưới Data\ như MultiCandidateSettings.
///
/// KHÔNG ghi nhớ khi người dùng bấm "Hủy" — Hủy nghĩa là "để tôi cân nhắc tiếp",
/// không phải một lựa chọn dứt khoát đáng nhớ lại; chỉ Thoát/Thu nhỏ mới hợp lý
/// để nhớ. Có thể bật lại hộp thoại từ Settings (SettingsViewModel.ResetExitPrompt)
/// nếu người dùng đổi ý sau này.
/// </summary>
public static class ExitBehaviorSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpGameAccelerator", "Data", "exit_behavior.json");

    private sealed class Model
    {
        public bool DontAskAgain { get; set; }
        public string Action { get; set; } = "";
    }

    /// <summary>null = vẫn phải hỏi (chưa tick "Không hỏi lại", hoặc file lỗi/chưa có).</summary>
    public static RememberedExitAction? GetRememberedAction()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
            if (model is { DontAskAgain: true } && Enum.TryParse<RememberedExitAction>(model.Action, out var action))
                return action;
        }
        catch
        {
            // File hỏng/không đọc được — coi như chưa ghi nhớ gì, an toàn hơn là vẫn hỏi lại.
        }
        return null;
    }

    public static void Remember(RememberedExitAction action)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model { DontAskAgain = true, Action = action.ToString() }));
        }
        catch
        {
            // Ghi lỗi thì lần sau vẫn hỏi lại — không chặn UI vì việc này.
        }
    }

    /// <summary>Xoá lựa chọn đã ghi nhớ — cho hộp thoại xác nhận thoát hỏi lại từ đầu.
    /// Gọi từ Settings khi người dùng muốn "phục hồi" hộp thoại này.</summary>
    public static void ResetToAlwaysAsk()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // Không xoá được thì thôi — không phải lỗi nghiêm trọng.
        }
    }
}
