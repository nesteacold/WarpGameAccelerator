# WARP Game Accelerator - Project Context

## 1. Tổng quan dự án (Overview)
- **Tên ứng dụng:** WARP Game Accelerator
- **Mục tiêu:** Ứng dụng Desktop 1-Click giúp giảm ping game thông qua việc điều khiển Cloudflare WARP (`warp-cli`) chạy ngầm, hỗ trợ split tunneling cho các process game cụ thể thông qua `Mihomo` (Clash Meta).
- **Công nghệ (Tech Stack):** 
  - .NET 8 (C#)
  - WinUI 3 (Windows App SDK 1.8)
  - Kiến trúc MVVM với `CommunityToolkit.Mvvm`
  - Ứng dụng Unpackaged (Self-contained) yêu cầu quyền Administrator để can thiệp routing và quản lý tiến trình.

## 2. Lịch sử Phát triển (Phases 1-4)
Dự án được xây dựng qua 4 giai đoạn, mỗi giai đoạn giải quyết một mảng kiến trúc cụ thể:

### Phase 1: Nền tảng & Cốt lõi (Core Logic)
- **Lỗi MC6000 (Xung đột WinForms):** Ban đầu dùng `System.Windows.Forms` cho khay hệ thống (System Tray) nhưng gây xung đột với WinFX. Đã loại bỏ hoàn toàn WinForms và tự viết lớp `TrayIconHelper` sử dụng Win32 API (`Shell_NotifyIcon`).
- **Ping Monitor:** Tạo luồng ngầm gửi truy vấn Ping (2s/lần). Lấy `1.1.1.1` làm chuẩn (baseline) và hỗ trợ tự động nhận diện IP game.

### Phase 2: Giao diện (WinUI 3) & Biên dịch
- **Lỗi XamlCompiler:** Nâng cấp SDK từ 1.5 lên 1.8, cài đặt Visual Studio 2022 Build Tools để biên dịch thành công XAML trên .NET 8.
- **Lỗi WMC9999 (Xaml Pass2 NullRef):** Xảy ra khi dùng Converter cho danh sách (`List<long>`) trong XAML. Đã khắc phục bằng cách binding trực tiếp với string (`PingDisplay`, v.v.).
- Thiết kế UI ban đầu: Sử dụng Glassmorphism (Mica), Dark Theme.

### Phase 3: Split Tunneling & Proxy Routing (Mihomo)
- **Mihomo (Clash Meta):** Quyết định đưa lõi Mihomo vào để bắt lưu lượng (traffic) của Game `.exe` đi qua cổng SOCKS5 cục bộ, từ đó đẩy thẳng vào luồng kết nối của Cloudflare WARP.
- Giải quyết bài toán "Chỉ tăng tốc game, giữ nguyên trình duyệt" một cách triệt để mà không cần cài đặt driver can thiệp sâu.

### Phase 4: Fix Bug & Production Ready (Final)
- **Kiến trúc Build (Single-File):** Cấu hình `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` kết hợp `<PublishSingleFile>true</PublishSingleFile>` và `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`. Giúp đóng gói toàn bộ thư viện WinUI 3 và .NET 8 vào 1 file `.exe` duy nhất (~100MB), chạy trực tiếp mọi máy tính không cần Runtime.
- **Rò rỉ bộ nhớ (Memory Leak) WinUI 3**: Khắc phục lỗi Crash `0xc000027b` bằng cách cấu trúc lại vòng đời `OnNavigatedTo` / `OnNavigatedFrom` để huỷ đăng ký Event.
- **Lỗi Unpackaged AppData**: Khắc phục văng app khi gọi `ApplicationData.Current.LocalSettings` (do app không có Package Identity) bằng cách ghi thẳng ra file `ping_targets.json`.
- **Hoàn thiện UI/UX**: Chống tràn chữ khi thu hẹp cửa sổ (`TextWrapping`), đổi hệ màu Active sang Xanh Lá, thêm hộp thoại cảnh báo chống thoát nhầm. Thêm bọc lỗi (try-catch) cho Storyboard.
- **Cú pháp Cloudflare WARP 2024+**: Cập nhật lệnh `warp-cli` ngầm trong mã nguồn. Phiên bản Cloudflare One Client mới đã đổi từ `warp-cli set-mode proxy` sang `warp-cli mode proxy`. App đã tự động gửi cả hai bộ lệnh (cũ và mới) để ép chế độ SOCKS5 hoạt động tương thích với mọi phiên bản WARP Client.

## 3. Kiến trúc Luồng Dữ liệu (Routing Flow)
1. User chọn Game `.exe`.
2. `MihomoService` khởi động cấu hình TUN/SOCKS5, lắng nghe tiến trình Game.
3. `WarpCliService` gọi `warp-cli connect` để mở đường hầm.
4. Traffic của Game -> Mihomo -> SOCKS5 -> WARP -> Đích.
5. Cửa sổ UI liên tục hiển thị chênh lệch (Delta) giữa Ping trực tiếp và Ping qua WARP.

## 4. Trạng thái hiện tại (v1.4.0)
- Dự án đạt trạng thái **Hoàn thiện 100%**. Không còn lỗi cảnh báo, không còn Crash, kiến trúc đã đóng băng (Production-Ready).
- Các bản vá lỗi UI/UX nhỏ đã được xử lý triệt để.

### Phase 5: Nâng cấp Trải nghiệm Người dùng & Đa ngôn ngữ (v1.3.1 -> v1.4.0)
- **Mở rộng Profile Game:** Thêm các tựa game phổ biến (League of Legends, Valorant, Counter-Strike 2) vào danh sách cấu hình mặc định.
- **Sửa lỗi Giao diện (UI/UX):** Khắc phục lỗi lệch Grid ở các Game Card có tên dài (CS2), đổi tab "Đang chạy" thành "Process", xử lý dứt điểm lỗi hiển thị Icon (bằng cách dùng thẻ `FontIcon` mặc định của Segoe Fluent thay vì ép font cũ). Tinh chỉnh lại các nút On/Off trong Settings.
- **Đa ngôn ngữ (Localization):** Xây dựng `LocalizationService` dựa trên Dictionary (VIE/ENG). Cho phép người dùng chuyển đổi ngôn ngữ ứng dụng ngay lập tức (Runtime) thông qua Data Binding trong kiến trúc MVVM mà không cần khởi động lại. Lựa chọn ngôn ngữ được lưu xuống file JSON độc lập.
- **Quản lý phiên bản:** Đọc Version động từ Assembly Attributes hiển thị trực tiếp trên giao diện (`MainWindow`, `SettingsPage`), đảm bảo đồng bộ với file `.csproj`.
