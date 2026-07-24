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
- 📊 **Ping Monitor Thực Thời**: Theo dõi chỉ số Ping (độ trễ ms) của máy chủ Game trước và sau khi Boost theo thời gian thực. Hỗ trợ thêm bớt máy chủ tùy chỉnh (Google, Cloudflare, Custom IP...).
- 🔄 **Auto-Update Trực Tiếp**: Tích hợp kiểm tra và tự động nâng cấp phiên bản mới nhất từ GitHub Releases chỉ bằng 1 cú click ngay trong ứng dụng.
- 🌐 **Đa Ngôn Ngữ (Bilingual)**: Chuyển đổi linh hoạt giữa **Tiếng Việt 🇻🇳** và **Tiếng Anh 🇺🇸** tức thì.
- 🎨 **Giao Diện Fluent Design Modern**: Thiết kế giao diện phẳng tối giản theo phong cách Windows 11 Dark Mode kết hợp hiệu ứng Glassmorphism sắc nét.
- 📌 **Khay Hệ Thống (System Tray)**: Tự động thu nhỏ xuống khay đồng hồ, bật/tắt nhanh kết nối hoặc tùy chọn khởi động cùng Windows.
- 📦 **Single File .EXE (Không Cần Cài Đặt)**: Gói gọn tất cả trong 1 file `.exe` duy nhất, chạy ngay không cần cài đặt rườm rà.

---

## 📥 Tải Về & Sử Dụng (Download & Installation)

1. Truy cập mục **[GitHub Releases](https://github.com/nesteacold/WarpGameAccelerator/releases)**.
2. Tải về file `WarpGameAccelerator.exe` phiên bản mới nhất.
3. Chạy file `WarpGameAccelerator.exe` dưới quyền Administrator (Run as Administrator) để khởi chạy ứng dụng.

> ⚠️ **Yêu cầu hệ thống:**
> - Hệ điều hành: Windows 10 / Windows 11 (64-bit).
> - Đã cài đặt sẵn **[Cloudflare WARP Client](https://1.1.1.1/)**.

---

## 🛠️ Dành Cho Nhà Phát Triển (For Developers)

### Công nghệ sử dụng:
- **Framework**: .NET 8.0 (WinUI 3 / Windows App SDK)
- **Architecture**: MVVM Pattern (CommunityToolkit.Mvvm)
- **Proxy Core**: Sing-Box / Mihomo Core tích hợp ngầm
- **CI/CD**: GitHub Actions (Tự động Build & Publish Release)

### Cách tạo Release phiên bản mới:
1. Mở file `.csproj` và cập nhật phiên bản (vd: `1.6.0`).
2. Chạy file `Create-Release.bat` ở thư mục gốc.
3. Nhập Tag phiên bản (vd: `v1.6.0`) và ghi chú thay đổi (Change Logs).
4. GitHub Actions sẽ tự động biên dịch và phát hành bản Release mới lên GitHub!

---

## 📄 Giấy Phép (License)

Dự án này được phân phối dưới giấy phép **MIT License**. Bạn có thể tự do chỉnh sửa và phát triển thêm.
