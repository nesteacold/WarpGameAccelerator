namespace WarpGameAccelerator.Models;

/// <summary>
/// Hồ sơ game — chứa tên thương mại và toàn bộ exe liên quan cần boost.
/// </summary>
public class GameProfile
{
    /// <summary>Tên hiển thị thân thiện, VD: "Age of Wulin"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Glyph icon Segoe MDL2, VD: "\uE7FC" (Controller)</summary>
    public string IconGlyph { get; set; } = "\uE7FC";

    /// <summary>Toàn bộ tên file exe cần định tuyến qua WARP (không phân biệt hoa/thường)</summary>
    public List<string> Executables { get; set; } = [];

    /// <summary>false = built-in, true = do người dùng tự thêm</summary>
    public bool IsCustom { get; set; }

    /// <summary>Kiểm tra xem một tên exe có thuộc profile này không</summary>
    public bool Matches(string exeName) =>
        Executables.Any(e => e.Equals(exeName, StringComparison.OrdinalIgnoreCase)
                          || e.Equals(exeName + ".exe", StringComparison.OrdinalIgnoreCase)
                          || (e + ".exe").Equals(exeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Danh sách exe dạng chuỗi phân cách bởi dấu phẩy, dùng để truyền cho MihomoService</summary>
    public string ExecutablesJoined => string.Join(", ", Executables);
}
