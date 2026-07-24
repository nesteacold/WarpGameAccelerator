# WARP Game Accelerator - Technical & UI Decision Log

## 1. Phân tích Nguyên nhân Game Disconnect khi dùng WARP

### Vấn đề:
Khi kết nối qua Cloudflare WARP (đặc biệt qua Local Proxy SOCKS5 `127.0.0.1:40000` của `warp-cli`), ping báo ổn định và không mất gói (0% loss), nhưng game thời gian thực lại bị ngắt kết nối ngẫu nhiên (disconnect).

### Nguyên nhân kỹ thuật:
1. **SOCKS5 UDP Session Timeout & Drop:** Cổng SOCKS5 của `warp-cli` quản lý bảng phiên UDP với timeout ngắn. Khi game vào màn hình chờ/loading và ngưng gửi gói UDP trong vài giây, `warp-svc` tự ngắt association SOCKS5 UDP, làm ngắt kết nối game.
2. **Xung đột MTU & Phân mảnh (Fragmentation):** Cấu hình TUN MTU 1420 cộng thêm overhead mã hóa SOCKS5/WireGuard làm gói tin bị nát (fragment). Máy chủ game thường từ chối gói UDP bị phân mảnh.
3. **Cloudflare Zero Trust (Cloudflare One) Local Proxy:** Trong bản Zero Trust, `Local proxy mode` **CHỈ hỗ trợ HTTP traffic** (không hỗ trợ UDP), đồng thời bắt buộc chạy trên giao thức **MASQUE** (dễ bị ISP Việt Nam bóp/chặn UDP).

---

## 2. Giải pháp Kiến trúc Hai Chế độ (Dual-Mode Architecture)

Dự án hỗ trợ 2 chế độ kết nối song song trong giao diện UI:

### Chế độ 1: Direct WireGuard (Khuyên dùng cho Game)
- **Cơ chế:** Mihomo (Clash Meta) giao tiếp trực tiếp với Cloudflare Edge via WireGuard protocol (sử dụng nhân `wireguard-go` tích hợp sẵn trong Mihomo). Bỏ qua hoàn toàn ứng dụng `warp-cli` và cổng SOCKS5.
- **Ưu điểm:**
  - Giảm 2-8ms ping trễ (loại bỏ IPC loopback 127.0.0.1).
  - Tích hợp `Persistent Keepalive = 25s`, chống ngắt kết nối UDP 100%.
  - Giảm MTU xuống `1280` phòng chống triệt để phân mảnh gói tin.
  - Người dùng không cần cài đặt app Cloudflare WARP gốc.

### Chế độ 2: WARP Client Proxy (Chế độ Tương thích)
- **Cơ chế:** Traffic -> Mihomo TUN -> SOCKS5 (127.0.0.1:40000) -> `warp-cli`.
- **Ưu điểm:** Tận dụng app WARP gốc có sẵn trên máy, phù hợp cho duyệt web hoặc khi không muốn đăng ký key WireGuard tự động.

---

## 3. Quy chuẩn Đặt tên UI (UI Naming Specification - Bộ 1)

Dành cho màn hình Cài đặt (Settings UI) của ứng dụng WinUI 3:

| Chế độ Kiến trúc | Tên hiển thị UI (Tiếng Việt) | Tên hiển thị UI (Tiếng Anh) | Tooltip / Description |
| :--- | :--- | :--- | :--- |
| **Direct WireGuard** | **Chế độ Siêu Tốc (Direct WireGuard)** *(Khuyên dùng)* | **Direct WireGuard (Recommended for Games)** | Khuyên dùng cho Game thời gian thực. Tối ưu Ping, chống rớt mạng, không cần cài app WARP. |
| **WARP-CLI SOCKS5** | **Chế độ Tương Thích (WARP Client)** | **WARP Client Proxy (Compatibility Mode)** | Dành cho duyệt Web / App thông thường. Yêu cầu ứng dụng Cloudflare WARP gốc. |

---
*Ghi chú: Tài liệu này được tổng hợp để chuyển tiếp tri thức sang conversation "Designing AOW Booster UI".*
