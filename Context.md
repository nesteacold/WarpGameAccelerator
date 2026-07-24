# SYSTEM SPECIFICATION: CLOUDFLARE WARP GAME BOOSTER

## 1. Tổng quan dự án (Project Overview)
* **Tên dự án:** WARP Game Accelerator
* **Nền tảng:** Windows 10 / 11 (x64) - Unpackaged App
* **Mục tiêu:** Xây dựng một ứng dụng Desktop dạng "1-Click" tối ưu hóa Ping game bằng cách kết hợp sức mạnh định tuyến của **Mihomo (Clash Meta)** và hạ tầng mạng của **Cloudflare WARP+**.
* **Bài toán giải quyết:** Khắc phục nhược điểm của Cloudflare WARP thông thường (bắt toàn bộ máy tính fake IP). Ứng dụng này cung cấp tính năng **Split Tunneling (Định tuyến chọn lọc)**: Chỉ đưa traffic của file `.exe` Game đi qua WARP, giữ nguyên mạng gốc tốc độ cao cho Chrome, YouTube.

---

## 2. Lịch sử Phát triển (Phases 1-4)
Vui lòng tham khảo tệp `project_context.md` để xem toàn bộ chi tiết về lịch sử phát triển và các lỗi đã giải quyết (Bao gồm khắc phục Memory Leaks, xử lý XamlCompiler, và xử lý lưu trữ tĩnh không cần quyền Administrator).