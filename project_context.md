# WARP Game Accelerator - Project Context

Đây là tài liệu tóm tắt toàn bộ bối cảnh, kiến trúc và quyết định kỹ thuật của dự án để AI hoặc lập trình viên có thể nắm bắt nhanh chóng khi làm việc ở các phiên làm việc tiếp theo.

## 1. Giới thiệu chung
- **Tên dự án:** WARP Game Accelerator
- **Công nghệ:** C# .NET 8 (WPF / WinUI 3), giao diện XAML.
- **Mục đích:** Tăng tốc độ trễ (Ping) cho các ứng dụng Game thông qua hạ tầng mạng Cloudflare WARP. Hỗ trợ **Split Tunneling** (chỉ định tuyến đúng những tiến trình (process) game người dùng chọn, các ứng dụng khác như Chrome, Discord vẫn dùng mạng mặc định).

## 2. Kiến trúc & Core Engine
Dự án sử dụng **Mihomo** (phiên bản nhánh của Clash Meta) làm nhân mạng lõi để bắt và định tuyến gói tin thông qua **Wintun** (Card mạng ảo). 

### 2.1. Quản lý File Core (Single-file Deployment)
- Từ bản **v1.6.7**, ứng dụng được đóng gói thành **1 file EXE duy nhất** (Single-file publish) thay vì phải đi kèm thư mục `Core`.
- Để lách luật Windows (không thể chạy trực tiếp một file exe đang bị nén chung với app chính), file `mihomo.exe` và các thành phần cốt lõi được set là **EmbeddedResource** trong file `.csproj`.
- Khi app khởi động, `MihomoService.cs` sẽ tự động giải nén file `mihomo.exe` vào thư mục tạm của hệ điều hành: `C:\Users\<User>\AppData\Local\WarpGameAccelerator\Core`. Sau đó nó sẽ sinh ra file `config.yaml` và gọi nhân Mihomo chạy ngầm tại đây.

### 2.2. Hai chế độ mạng (Engine Modes)
Ứng dụng có 2 chế độ hoạt động, cấu hình tự sinh trong `MihomoService.cs`:

1. **Game Mode (Direct WireGuard) 🔥 Khuyên dùng:**
   - **Hoạt động:** Mihomo TUN bắt gói tin -> Mihomo tự mã hóa gói tin bằng giao thức WireGuard -> Gửi thẳng ra `1.1.1.1` của Cloudflare.
   - **Ưu điểm:** Bỏ qua hoàn toàn ứng dụng WARP gốc, giảm phân mảnh gói tin, tối ưu tuyệt đối độ trễ (Ping).
   - **Tài khoản WARP:** Ứng dụng tự động gọi API của Cloudflare (giả lập thiết bị Android) thông qua `WarpAccountService.cs` để xin cấp khóa Private/Public Key và IP ảo miễn phí. Lưu vào `Data/warp_account.json`.

2. **Chế độ Tương Thích (WARP Client Proxy):**
   - **Hoạt động:** Mihomo TUN bắt gói tin -> Bắn vào cổng SOCKS5 nội bộ `127.0.0.1:40000` -> Ứng dụng Cloudflare WARP (`warp-svc.exe`) trên máy hứng gói tin -> Mã hóa WireGuard -> Gửi ra `1.1.1.1`.
   - **Yêu cầu:** Máy tính phải cài đặt sẵn và bật ứng dụng Cloudflare WARP chính chủ.

### 2.3. Cấu hình DNS Fake-IP
Cả 2 chế độ đều bắt buộc sử dụng tính năng **Fake-IP** của Mihomo (`enhanced-mode: fake-ip`, `fake-ip-range: 198.18.0.1/16`) kết hợp với lệnh `dns-hijack: any:53`. 
- Nếu không có DNS Fake-IP, các game hoặc trình duyệt sẽ không thể phân giải được tên miền để lấy IP trước khi kết nối TCP/UDP, dẫn đến lỗi rớt mạng (`ERR_CONNECTION_CLOSED` trên trình duyệt).

## 3. GitHub Actions (CI/CD)
- Dự án đã được thiết lập quy trình tự động hóa tại `.github/workflows/release.yml`.
- Mỗi khi có một Tag Git mới bắt đầu bằng chữ `v` được push lên nhánh chính (Ví dụ: `git tag v1.7.0` & `git push --tags`), máy chủ GitHub sẽ tự động:
  - Biên dịch toàn bộ mã nguồn (.NET 8 Publish).
  - Xuất ra 1 file `.exe` duy nhất.
  - Tự động tạo một Release mới trên trang GitHub và đính kèm file `.exe` vào đó.

## 4. Lưu ý khi phát triển tiếp
- Các chuỗi ngôn ngữ được quản lý trong `LocalizationService.cs`.
- Mọi logic sinh file cấu hình proxy nằm trong `MihomoService.cs`.
- Quản lý tài khoản và API Cloudflare nằm trong `WarpAccountService.cs`.
