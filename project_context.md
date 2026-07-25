# WARP Game Accelerator — Project Context

> Tài liệu tóm tắt toàn bộ kiến trúc, quyết định kỹ thuật và lịch sử tính năng.  
> Cập nhật lần cuối: **v1.8.4**

---

## 1. Giới thiệu chung

| Mục | Thông tin |
|---|---|
| **Tên dự án** | WARP Game Accelerator |
| **Công nghệ** | C# .NET 8, WinUI 3, XAML |
| **GitHub** | `nesteacold/WarpGameAccelerator` |
| **Phiên bản hiện tại** | v1.8.4 |
| **Mục đích** | Tăng tốc Ping game qua hạ tầng Cloudflare WARP. Hỗ trợ Split Tunneling — chỉ định tuyến đúng process game, các app khác vẫn dùng mạng mặc định. |

---

## 2. Kiến trúc & Core Engine

### 2.1. Nhân mạng (Mihomo)
- Dùng **Mihomo** (fork Clash Meta) làm proxy engine bắt và định tuyến gói tin qua **Wintun** (card mạng ảo).
- Từ v1.6.7: đóng gói thành **1 file EXE duy nhất** (Single-file publish).
- `mihomo.exe` được set là **EmbeddedResource** trong `.csproj`, giải nén ra `AppData\Local\WarpGameAccelerator\Core\` khi khởi động.

### 2.2. Hai chế độ mạng

**1. Game Mode (Direct WireGuard) 🔥 Khuyên dùng**
- Luồng: Mihomo TUN → mã hóa WireGuard → gửi thẳng `162.159.192.1:2408` (Cloudflare).
- Bỏ qua ứng dụng WARP gốc → giảm phân mảnh gói tin → ping tối ưu nhất.
- Tài khoản WARP tự động đăng ký qua API Cloudflare (giả lập Android) trong `WarpAccountService.cs`.

**2. Chế độ Tương Thích (WARP Client Proxy)**
- Luồng: Mihomo TUN → SOCKS5 `127.0.0.1:40000` → `warp-svc.exe` → Cloudflare.
- Yêu cầu cài Cloudflare WARP chính chủ trên máy.

### 2.3. DNS Fake-IP
- Cả 2 chế độ dùng Fake-IP (`enhanced-mode: fake-ip`, range `198.18.0.1/16`) + `dns-hijack: any:53`.
- Bắt buộc để game có thể resolve tên miền trước khi kết nối TCP/UDP.

---

## 3. Cấu trúc Services chính

| Service | Trách nhiệm |
|---|---|
| `MihomoService.cs` | Sinh `config.yaml`, giải nén core, quản lý vòng đời Mihomo process |
| `WarpAccountService.cs` | Gọi API Cloudflare đăng ký device, lưu WireGuard key, quản lý WARP+ license |
| `MultiClientService.cs` | WMI detect token game, launch nhiều client AOW, quản lý process PID |
| `WarpCliService.cs` | Wrapper cho `warp-cli.exe` (chế độ tương thích) |
| `PingMonitorService.cs` | Đo Ping realtime hiển thị trên Dashboard |
| `ProcessService.cs` | Liệt kê và chọn process game để định tuyến |
| `GameProfileService.cs` | Lưu/đọc profile game (tên + IP server) |
| `NetworkOptimizerService.cs` | Tối ưu MTU, TCP buffer |
| `UpdateService.cs` | Tự động kiểm tra và tải bản mới từ GitHub Releases |
| `LocalizationService.cs` | Đa ngôn ngữ (VIE/ENG), hot-swap không cần restart |

---

## 4. Navigation (MainWindow)

### Menu Items (trên)
| Tag | Trang | Icon |
|---|---|---|
| `dashboard` | DashboardPage | E945 (Speedometer) |
| `process` | ProcessPickerPage | E7FC (Gamepad) |

### Footer Items (dưới, từ trên xuống)
| Tag | Trang | Icon |
|---|---|---|
| `multiclient` | MultiClientPage | E90F (People) |
| `warpaccount` | WarpAccountPage | E8D4 (Account) |
| `settings` | SettingsPage | E713 (Gear) |
| _(exit)_ | — | E711 (Close, đỏ) |

> **Quan trọng:** Index FooterMenuItems = `[0]=multiclient, [1]=warpaccount, [2]=settings, [3]=exit`  
> Khi thêm item mới vào Footer phải cập nhật index trong `UpdateNavItemLabels()`.

---

## 5. Lưu trữ dữ liệu

Tất cả dữ liệu người dùng lưu trong `AppData\Local\WarpGameAccelerator\`:

| File | Nội dung |
|---|---|
| `Core\mihomo.exe` | Nhân Mihomo (giải nén từ EmbeddedResource) |
| `Core\config.yaml` | Cấu hình proxy sinh tự động |
| `Data\warp_account.json` | WireGuard keys, IP, WARP+ License key |
| `Data\aow_token.json` | Token game AOW cho Multi-Client Launcher |

> ⚠️ **Trước v1.8.2**: `warp_account.json` lưu cạnh `.exe` → bị mất khi update.  
> **Từ v1.8.2**: Lưu trong AppData → tồn tại vĩnh viễn qua mọi lần cập nhật.

---

## 6. WARP+ Account

- Màn hình: `WarpAccountPage.xaml`
- API: `PUT https://api.cloudflareclient.com/v0a2158/reg/{id}/account` với `Bearer {token}`
- Khi re-register tài khoản mới, license cũ được **tự động re-apply** (fix v1.8.2).
- Nút **"Gỡ Key & Reset về WARP Free"**: xóa `warp_account.json`, tạo tài khoản mới khi Boost lần sau.

---

## 7. Multi-Client Launcher (AOW)

- Màn hình: `MultiClientPage.xaml`
- Service: `MultiClientService.cs`
- **Luồng 3 bước:**
  1. Chọn thư mục AOW → tự động quét đệ quy subfolder tìm `fxlaunch.exe` + `fxgame.exe` (tự ghi nhớ đường dẫn).
  2. Bấm "Mở Client Đầu" → gọi `fxlaunch.exe`
  3. Bấm "Detect Token" → WMI query `Win32_Process` CommandLine của `fxgame.exe` → parse token → lưu `aow_token.json`
  4. Nhập số lượng → `Process.Start(fxgame.exe, token)` × N lần
- Token được lưu lại, dùng đến khi game update phiên bản.
- Giới hạn tối đa 10 client.

---

## 8. CI/CD (GitHub Actions)

- File: `.github/workflows/release.yml`
- Trigger: Push tag `v*` → tự động build + tạo GitHub Release + đính kèm `.exe`.
- **Auto-version**: Workflow inject version từ tên tag vào binary (`-p:Version=X.Y.Z`).
- Quy trình release: `git tag vX.Y.Z && git push --tags` là đủ.

---

## 9. Lịch sử phiên bản (tóm tắt)

| Phiên bản | Thay đổi chính |
|---|---|
| v1.5.0 | Phiên bản đầu |
| v1.6.0 | Connection Engine Mode |
| v1.6.1 | Direct WireGuard (Game Mode) |
| v1.6.9 | Mihomo Single-file, Game Mode mặc định |
| v1.7.0 | WARP+ Account screen |
| v1.7.3 | Fix crash DI, fix version display |
| v1.8.0 | Multi-Client Launcher cho AOW |
| v1.8.1 | Fix folder picker crash (Win32 SHBrowseForFolder), fix icon |
| v1.8.2 | Fix mất WARP+ key sau update (AppData migration + auto re-apply) |
| v1.8.4 | Fix launcher file (`fxlaunch.exe`), bổ sung quét thư mục đệ quy, tự động lưu đường dẫn game |
| v1.8.9 | Khôi phục cấu hình TUN v1.5.0 (`stack: mixed`, `mtu: 1280`, loại bỏ hoàn toàn `fake-ip` gây lỗi cURL SSL Error trong addon game) |
| v1.8.10 | Fix dứt điểm crash app khi mở nhiều client (bọc try-catch xung quanh process handle & timer UI update) |
| v1.9.0 | Bổ sung bảng Chọn Node Server kiểu GearUP (Auto, Taiwan, HK, Singapore...), tự động bắt IP Game Server & đo TCP Handshake Ping thực tế, nới rộng UI 520x680 & căn giữa màn hình |
| v1.9.1 | Fix triệt để crash khi mở nhiều client (tạm dừng UI timer khi launch, giải phóng handle process) và tự động ghi log lỗi vào AppData/Local/WarpGameAccelerator/Logs/crash.log |
