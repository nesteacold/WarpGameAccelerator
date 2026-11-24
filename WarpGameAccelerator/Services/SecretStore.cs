// ============================================================
// Services/SecretStore.cs — ứng viên #30: mã hoá lúc nghỉ (at-rest) cho bí mật app tự sở hữu
//
// Vì sao tồn tại (đo 2026-11-01, batch 45, `experiments/../count` trên Data\):
//   game_folders.json  : 3 token phiên × 192 ký tự, trong đó 2 giá trị GIỐNG HỆT nhau
//   aow_token.json     : 1 token 192 ký tự (đường cũ, chỉ còn đọc để migrate)
//   warp_account(.masque).json, bug_report_config.json : vẫn còn plaintext — CHƯA đụng ở đây, xem
//   lý do ở ghi chú cuối file.
//
// Vì sao DPAPI mà không phải thư viện khác: `CryptProtectData` với scope CURRENT_USER là có sẵn
// trong crypt32.dll ⇒ không thêm package, không đổi luồng build (Hiến pháp VI: một đường build),
// và khoá gắn với hồ sơ người dùng đang đăng nhập — đúng mô hình đe doạ ở đây (một tiến trình khác
// cùng tài khoản đọc trộm tệp, hoặc tệp bị mang theo trong backup).
//
// Ba ràng buộc thiết kế, mỗi cái đều có test trong experiments/secret-store:
//   1. TƯƠNG THÍCH NGƯỢC — tệp cũ plaintext vẫn đọc được, không ai mất token khi cập nhật app.
//   2. KHÔNG BAO GIỜ làm mất dữ liệu — nếu API lỗi hoặc blob hỏng, trả lại nguyên văn để luồng
//      gọi còn dùng được giá trị cũ, thay vì ném và làm hỏng việc lưu.
//   3. KHÔNG KÉP DẤU — Protect(Protect(x)) == Protect(x), vì các chỗ ghi thỉnh thoảng ghi lại
//      giá trị vừa đọc ra.
// ============================================================
using System.Runtime.InteropServices;
using System.Text;

namespace WarpGameAccelerator.Services;

internal static class SecretStore
{
    /// <summary>Đầu ra luôn mang dấu này nên phân biệt được với bản cũ, và đổi định dạng trong tương lai được.</summary>
    internal const string Marker = "v1:dpapi:";

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDescription,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, out IntPtr ppSzDescription,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string s) => s.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>Bọc bằng DPAPI; trả về nguyên văn nếu trống/hỏng API/đã được bảo vệ rồi.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var inBlob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        var outBlob = default(DATA_BLOB);
        try
        {
            Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
            if (!CryptProtectData(ref inBlob, "WarpGameAccelerator", IntPtr.Zero, IntPtr.Zero,
                                  IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return plain;                                   // ràng buộc 2: thà để plaintext còn hơn mất dữ liệu
            var got = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, got, 0, outBlob.cbData);
            return Marker + Convert.ToBase64String(got);
        }
        catch { return plain; }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>Ngược lại; bản plaintext cũ đi qua nguyên văn (ràng buộc 1).</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored)) return stored;
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        try
        {
            var raw = Convert.FromBase64String(stored.Substring(Marker.Length));
            inBlob = new DATA_BLOB { cbData = raw.Length, pbData = Marshal.AllocHGlobal(raw.Length) };
            Marshal.Copy(raw, 0, inBlob.pbData, raw.Length);
            if (!CryptUnprotectData(ref inBlob, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return stored;                                  // blob hỏng/không cùng hồ sơ ⇒ để nguyên, không ném
            var got = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, got, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(got);
        }
        catch { return stored; }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}

// CHƯA bọc ở batch này, và vì sao:
//  - warp_account.json / warp_masque_account.json: `PrivateKey`+`License` được NHIỀU chỗ đọc
//    (MihomoService sinh YAML tại :644/:716/:753/:862, MasqueEndpointLab :171, PersonalVpnService),
//    và được ghi bởi luồng đăng ký tài khoản — đổi một chiều sẽ làm hỏng đường đang chạy, cần
//    migration có test trên máy có tài khoản thật.
//  - bug_report_config.json: `WebhookUrl` do CHÍNH người dùng dán vào tay; mã hoá sẽ làm mất khả
//    năng tự sửa đó. Đánh đổi này nên để chủ máy quyết, không nên âm thầm đổi.
