# 🚀 WARP Game Accelerator

<p align="center">
  <img src="WarpGameAccelerator/Assets/logo.ico" width="100" alt="WARP Game Accelerator Logo" />
</p>

<p align="center">
  <b>Ứng dụng tăng tốc Game và tối ưu kết nối mạng dựa trên Cloudflare WARP+ với công nghệ Split Tunneling độc quyền.</b>
</p>

<p align="center">
  <a href="https://github.com/nesteacold/WarpGameAccelerator/releases"><img src="https://img.shields.io/github/v/release/nesteacold/WarpGameAccelerator?color=orange&style=flat-square" alt="Latest Release"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/8.0"><img src="https://img.shields.io/badge/.NET-8.0-blueviolet?style=flat-square" alt=".NET 8"></a>
  <a href="https://learn.microsoft.com/windows/apps/winui/winui3/"><img src="https://img.shields.io/badge/UI-WinUI%203-blue?style=flat-square" alt="WinUI 3"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green?style=flat-square" alt="License"></a>
</p>

---

## 🌟 Tính Năng Nổi Bật (Features)

- ⚡ **Split Tunneling (Tăng tốc chọn lọc)**: Chỉ định tuyến duy nhất Game/Process bạn chọn qua tuyến đường ưu tiên Cloudflare WARP+. Toàn bộ các ứng dụng khác như *Discord, YouTube, Web Browser* vẫn giữ nguyên mạng gốc, không gây ảnh hưởng đến gián đoạn cuộc gọi thoại hay giật lag video.
- 🔌 **Direct Mode (WireGuard trực tiếp)**: Kết nối thẳng vào hạ tầng WireGuard của Cloudflare WARP, không cần chạy song song client WARP chính thức. Tài khoản WARP được **tự động đăng ký hoàn toàn** ngay từ lần Boost đầu tiên — không cần đăng nhập, không cần thao tác gì thêm.
- 📥 **Import Account thủ công**: Người dùng nâng cao có thể import tài khoản WARP có sẵn (tạo bằng [wgcf](https://github.com/ViRb3/wgcf)) qua Settings → WARP+ Account, thay cho tài khoản tự đăng ký.
- 🕹️ **Multi-Client cho Age of Wushu**: Mở nhiều client game cùng lúc chỉ với vài cú click — tự động detect token đăng nhập từ tiến trình đang chạy, tự thêm delay an toàn giữa các lần mở để tránh bị anticheat chặn.
- 🛡️ **Tự động khôi phục sau khi crash**: App ghi nhớ profile/process đang Boost — nếu bị tắt bất ngờ, lần mở lại sau sẽ tự động Boost lại đúng game đang chơi.
- 📊 **Ping Monitor Thực Thời**: Theo dõi chỉ số Ping (độ trễ ms) của máy chủ Game trước và sau khi Boost theo thời gian thực. Hỗ trợ thêm bớt máy chủ tùy chỉnh (Google, Cloudflare, Custom IP...).
- 🎮 **Hỗ trợ nhiều Game**: Có sẵn profile cho Age of Wushu, League of Legends, Valorant, Counter-Strike 2 — hoặc tự thêm process bất kỳ.
- 🔄 **Auto-Update Trực Tiếp**: Tích hợp kiểm tra và tự động nâng cấp phiên bản mới nhất từ GitHub Releases chỉ bằng 1 cú click ngay trong ứng dụng.
- 🐞 **Báo lỗi tự động**: Crash được ghi log và tự động gửi báo cáo lên GitHub Issues của dự án để đội phát triển xử lý nhanh hơn.
- 🌐 **Đa Ngôn Ngữ (Bilingual)**: Chuyển đổi linh hoạt giữa **Tiếng Việt 🇻🇳** và **Tiếng Anh 🇺🇸** tức thì.
- 🎨 **Giao Diện Fluent Design Modern**: Thiết kế giao diện phẳng tối giản theo phong cách Windows 11 Dark Mode kết hợp hiệu ứng Glassmorphism sắc nét.
- 📌 **Khay Hệ Thống (System Tray)**: Tự động thu nhỏ xuống khay đồng hồ, bật/tắt nhanh kết nối hoặc tùy chọn khởi động cùng Windows.
- 📦 **Single File .EXE (Không Cần Cài Đặt)**: Gói gọn tất cả trong 1 file `.exe` duy nhất, chạy ngay không cần cài đặt rườm rà.

---

## 📥 Tải Về & Sử Dụng (Download & Installation)

1. Truy cập mục **[GitHub Releases](https://github.com/nesteacold/WarpGameAccelerator/releases)**.
2. Tải về file `WarpGameAccelerator.exe` phiên bản mới nhất.
3. Chạy file `WarpGameAccelerator.exe` dưới quyền Administrator (Run as Administrator) để khởi chạy ứng dụng — quyền Admin là bắt buộc để tạo network adapter (TUN) cho WireGuard.

> ⚠️ **Yêu cầu hệ thống:**
> - Hệ điều hành: Windows 10 / Windows 11 (64-bit).
> - **Direct Mode** (mặc định, khuyên dùng): không cần cài gì thêm — tài khoản WARP được tự tạo ngầm.
> - **Chế độ Tương Thích** (fallback, dùng proxy SOCKS5): cần cài sẵn **[Cloudflare WARP Client](https://1.1.1.1/)**.

---

## 🛠️ Dành Cho Nhà Phát Triển (For Developers)

### Công nghệ sử dụng:
- **Framework**: .NET 8.0 (WinUI 3 / Windows App SDK), self-contained win-x64
- **Architecture**: MVVM Pattern (CommunityToolkit.Mvvm), Dependency Injection
- **Proxy Core**: [Mihomo](https://github.com/MetaCubeX/mihomo) (nhúng sẵn) — sinh config WireGuard/TUN động
- **Đăng ký tài khoản WARP**: [wgcf](https://github.com/ViRb3/wgcf) (nhúng sẵn, chạy ngầm)
- **CI/CD**: GitHub Actions (Tự động Build & Publish Release)

### Cách tạo Release phiên bản mới:
1. Mở file `WarpGameAccelerator/WarpGameAccelerator.csproj` và cập nhật `<Version>`/`<AssemblyVersion>`/`<FileVersion>` (vd: `1.10.0`).
2. Commit thay đổi, sau đó chạy `Create-Release.bat` ở thư mục gốc (hoặc tự `git tag vX.Y.Z && git push origin main --tags`).
3. GitHub Actions sẽ tự động biên dịch (`dotnet publish`) và phát hành bản Release mới kèm ghi chú thay đổi tự sinh từ commit log.

### Build & chạy local:
```bash
dotnet build WarpGameAccelerator.sln -c Debug
dotnet publish WarpGameAccelerator/WarpGameAccelerator.csproj -c Release -r win-x64 --self-contained -o ./Publish
```

---

## 📄 Giấy Phép (License)

Dự án này được phân phối dưới giấy phép **MIT License**. Bạn có thể tự do chỉnh sửa và phát triển thêm.
