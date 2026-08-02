# WarpGameAccelerator

C# .NET 8 / WinUI 3 desktop app. Tăng tốc & chống rớt mạng game (Age of Wushu) qua Cloudflare WARP, dùng **Mihomo** (fork Clash Meta) làm engine TUN + Split Tunneling theo process — chỉ traffic của game đi qua tunnel, các app khác giữ mạng gốc.

Version hiện tại: xem `<Version>` trong `WarpGameAccelerator/WarpGameAccelerator.csproj` (v1.10.2 tại thời điểm viết file này).

## Build & chạy thử (test cục bộ)

**Chỉ dùng lệnh CLI này để build** — không dùng Visual Studio (mở solution bằng VS tạo ra output ở path KHÁC `bin\x64\Debug\...`, gây nhầm lẫn "sao sửa rồi mà vẫn chạy bản cũ"):

```bash
dotnet build WarpGameAccelerator/WarpGameAccelerator.csproj -c Debug -r win-x64
```

**File exe để chạy test luôn là** (path cố định, không đổi giữa các lần build):
```
WarpGameAccelerator\bin\Debug\net8.0-windows10.0.19041.0\win-x64\WarpGameAccelerator.exe
```

Trước khi build lại, **phải đóng hẳn app đang chạy** — nếu không MSBuild báo lỗi file bị khoá (`MSB3027`/`MSB3021`). Nếu từng build bằng Visual Studio trước đó, xoá thư mục `bin\x64\` (build output riêng của VS, không liên quan tới path trên) để tránh chạy nhầm bản cũ.

## Kiến trúc mạng

Hai chế độ, chọn ở Settings:

1. **Direct WireGuard (Game Mode, khuyên dùng)** — Mihomo TUN mã hoá WireGuard gửi thẳng Cloudflare Edge (`162.159.192.1:2408`), không qua app WARP gốc. Ping thấp nhất, không rớt UDP.
2. **WARP Client Proxy (Tương thích)** — Mihomo TUN → SOCKS5 `127.0.0.1:40000` → `warp-svc.exe` (WARP gốc cài trên máy). Dùng khi không muốn tự đăng ký WireGuard key.

**Bắt buộc trong `config.yaml` sinh bởi `MihomoService.cs`** (đã verify thực nghiệm, KHÔNG đổi lại nếu không có bằng chứng mới):
- `tun.stack: mixed` — **KHÔNG dùng `gvisor`** (lỗi tương thích TLS/cURL trong addon game).
- `tun.find-process-mode: always` — **KHÔNG dùng `strict`** (mặc định), vì một số kết nối TUN không attribute được process, fallback `MATCH,DIRECT` rồi timeout ~20s → traffic "rò" ra ngoài tunnel. Log nhận diện lỗi này: `dial DIRECT (match Match/) ... i/o timeout` không có tên process trong ngoặc.
- **KHÔNG dùng `DOMAIN-SUFFIX` để vá lỗi process-detection** — phá vỡ Split Tunneling, có thể đẩy traffic duyệt web (không phải game) vào tunnel WARP+ giới hạn băng thông. Nếu traffic rò ra ngoài tunnel, sửa `find-process-mode`, không thêm domain rule.
- WireGuard block cần `keepalive: 25` (chống rớt UDP khi NAT timeout) và `udp: true`.
- `inet4-route-exclude-address` phải là **raw IP**, không phải hostname — mihomo crash fatal khi parse hostname. Chỉ set field này `if (IPAddress.TryParse(host, out _))`.
- DNS: `enhanced-mode: fake-ip`, range `198.18.0.1/16`, `dns-hijack: any:53` — bắt buộc để game resolve domain trước khi kết nối.

## WARP Account (`WarpAccountService.cs`)

- **Tài khoản WARP tự đăng ký qua HTTP API thường bị Cloudflare gán chính sách MASQUE-only** — mihomo không hỗ trợ MASQUE → Direct Mode không bao giờ handshake được dù config đúng.
- **Fix**: đăng ký qua `wgcf.exe` (embedded resource, extract ra `Core\wgcf.exe`) — tài khoản wgcf giữ chính sách WireGuard cổ điển. `GetOrCreateAccountAsync()` ưu tiên `RegisterViaWgcfAsync()`, fallback `RegisterNewWarpAccountAsync()` (raw API) nếu wgcf lỗi.
- Raw API fallback: version string đúng là `v0a1922` (không phải version cũ `v0i...`), User-Agent `okhttp/3.12.1`, `type: Android`. Sau khi register phải PATCH `warp_enabled: true`, nếu không handshake sẽ im lặng thất bại.
- WARP+ license: `PUT /v0a.../reg/{id}/account` với Bearer token; re-register vẫn tự re-apply license cũ.

## Multi-Client Launcher (`MultiClientService.cs`, `Views/MultiClientPage.xaml`)

- Semantics UI: người dùng nhập **tổng số cửa sổ muốn có** (không phải "mở thêm N"), 1 nút duy nhất xử lý toàn bộ luồng (detect token nếu chưa có → launch đủ số còn thiếu).
- **`fxgame.exe` tự kill parent process của nó** (cơ chế game tự đóng launcher `fxlaunch.exe` sau khi vào game) — nếu `Process.Start(fxgame.exe)` trực tiếp từ WarpGameAccelerator, app chính bị kill theo. **Fix**: launch qua process con trung gian — `LauncherHelper.LaunchGameViaHelper()` spawn lại chính `WarpGameAccelerator.exe` với arg đặc biệt (`Program.cs` bắt arg này TRƯỚC khi khởi tạo `App`/DI, tránh tạo `MihomoService` thứ hai), process con đó đứng làm "cha giả" hứng chịu kill, `WaitForExit(90_000ms)`.
- Chờ client đăng nhập xong dùng `TcpTableHelper` (P/Invoke `GetExtendedTcpTable`) để detect kết nối TCP thật ra server, **không dùng fixed delay đoán mò**.
- `MinLaunchIntervalMs = 3000` là **floor tối thiểu** giữa các lần launch (không phải cơ chế chờ chính) — đừng rút ngắn hơn.
- Luôn `Dispose()` mỗi `Process` object sau khi dùng xong (tránh leak handle khi đếm/list process).

## Process lifecycle & crash-safety

- **`async void` event handler không có try/catch → exception crash cả process, không handler nào (kể cả `AppDomain.UnhandledException`) bắt được nếu kill đến từ `TerminateProcess` của process khác.** Mọi button click handler phải wrap try/catch, ghi vào `CrashReportService` + `DiagnosticLogService.Trace`.
- Debug "app biến mất không rõ lý do": dùng **SilentProcessExit + IFEO** (`HKLM\...\SilentProcessExit\<exe>`, `GlobalFlag=512`), không chỉ dựa vào Event Viewer / WER thông thường — WER và managed exception handler đều KHÔNG fire khi bị kill sạch từ process khác.
- **Mihomo cố ý sống sót khi app chính crash bất ngờ** (giữ game không bị disconnect giữa chừng) — chỉ kill mihomo khi user bấm Exit tường minh (`MainWindow.ExitApp()`, thứ tự: `StopProxy()` trước `DisconnectAsync()` để tránh timeout làm mihomo bị mồ côi ngay cả khi exit chủ động). **Không** thêm `AppDomain.ProcessExit` hay update-flow kill mihomo tự động — đã thử và bị revert theo yêu cầu người dùng.

## WireGuard for Windows conflict (`WireGuardConflictGuard.cs`)

- Một VPN cá nhân (WireGuard for Windows chạy dạng Windows Service `WireGuardTunnel$*`, dùng để remote-access vào máy khi Chrome Remote Desktop lỗi) có thể xung đột tầng thấp (TUN/WFP) với Mihomo, gây mất kết nối chập chờn khó lường cả khi chơi game lẫn duyệt web bình thường — dấu hiệu: ping/loss trong app vẫn báo bình thường nhưng `mihomo_runtime.log` đầy `context deadline exceeded` cho traffic thật.
- Fix: `PauseAsync()` (gọi lúc Start Boost) tự phát hiện mọi service `WireGuardTunnel$*` đang Running và dừng tạm; `ResumeAsync()` (gọi lúc Stop Boost) bật lại đúng service đã dừng. Không hardcode tên tunnel cụ thể.
- **KHÔNG thêm cơ chế tự resume ở lần khởi động app kế tiếp** nếu bị bỏ dở do app crash lúc đang Boost — Mihomo cố ý sống sót sau crash để không ngắt game (xem mục Process lifecycle bên dưới); tự resume ngay lúc mở lại app trong khi Mihomo vẫn đang chạy ngầm phục vụ game sẽ tái tạo đúng xung đột ban đầu NGAY LÚC đang chơi. Đã thử và bị yêu cầu bỏ.

## Network Optimizer (`NetworkOptimizerService.cs`)

- **Không đổi MTU qua `netsh`** — từng gây mất mạng tạm thời mỗi lần bật/tắt Boost và MTU bị kẹt vĩnh viễn ở 1420 nếu app bị kill giữa chừng (backup từng chỉ lưu RAM). Chỉ chỉnh `TcpAckFrequency`/`TcpNoDelay` qua registry, backup ra `network_backup.json` (persist thật, không phải RAM).
- `RecoverPendingChangesAsync()` chạy 1 lần lúc khởi động app: khôi phục backup registry dở dang từ session crash trước, và dọn MTU-1420-kẹt còn sót từ bản cũ (chỉ sửa interface đúng bằng 1420, set lại 1500).

## Release process

```bash
git tag vX.Y.Z
git push origin main --tags
```
GitHub Actions (`.github/workflows/release.yml`) tự build + publish GitHub Release khi push tag `v*`. Inject version vào binary từ tên tag.

## Data storage

Tất cả trong `%LocalAppData%\WarpGameAccelerator\`:
- `Core\mihomo.exe`, `Core\wgcf.exe` — extract từ EmbeddedResource lúc khởi động, có version-check để skip extract lại nếu không đổi.
- `Core\config.yaml` — sinh tự động mỗi lần Boost.
- `Data\warp_account.json` — WireGuard keys, WARP+ license (từ v1.8.2, **không** lưu cạnh `.exe` vì bị mất khi update).
- `Data\aow_token.json` — token AOW cho Multi-Client.
- `network_backup.json` — backup registry TCP settings.

## Quy ước code

- DI: `Microsoft.Extensions.DependencyInjection`, service đăng ký Singleton trong `App.xaml.cs`.
- MVVM: `CommunityToolkit.Mvvm`.
- Đa ngôn ngữ VIE/ENG qua `LocalizationService` (hot-swap không cần restart).
- Footer nav index cố định `[0]=multiclient, [1]=warpaccount, [2]=settings, [3]=exit` trong `MainWindow` — thêm item mới phải cập nhật `UpdateNavItemLabels()`.

## Ranh giới với dự án khác

`C:\Users\neste\Documents\DXVK_Project\wrapper\` là một project C++/CMake **riêng, độc lập** (d3d9.dll wrapper cho FSR/Frame-Gen/Eco Mode, do agent khác phụ trách). Nếu tích hợp UI điều khiển nó vào WarpGameAccelerator: chỉ viết code C#/XAML ở đây để cài đặt/gỡ/dọn log (orchestrate file có sẵn), **không sửa file `.cpp`/`.h` hay `installer/AOWLauncher/Program.cs`** bên project đó.

## AoW Booster (`Services/DxvkBoosterService.cs`, `Views/AowBoosterPage.xaml`)

Màn hình UI thay thế menu CLI của `AOWLauncher` (dự án `DXVK_Project`), orchestrate bằng cách điều khiển file `.exe` có sẵn qua `Process` + stdin/CLI-arg — **không sửa** `Program.cs` bên đó.

- Nguồn file `AOW_DXVK302_Launcher.exe`: **chính thức là `DXVK_Project\installer\AOW_DXVK302_Launcher.exe`** (bản publish tay của agent bên đó) — **không dùng** bản sinh ra từ `dotnet build`/`dotnet publish` trong `installer\AOWLauncher\bin\...` (đó chỉ là output build thường/publish thử của mình, có thể thiếu payload hoặc không đồng bộ). Copy đè thủ công vào `WarpGameAccelerator\Core\AOW_DXVK302_Launcher.exe` mỗi khi agent bên kia release bản mới (chưa có cơ chế tự đồng bộ), rồi build lại WarpGameAccelerator để nhúng embedded resource mới.
- Kiểm tra size sau khi copy: bản đúng phải ~16MB (self-contained single-file + 5 payload embedded). Nếu thấy vài trăm KB là copy nhầm output `dotnet build` (thiếu `.dll`/dependency đi kèm, chạy sẽ crash ngay).
- `uninstall` gọi CLI arg `uninstall` trực tiếp (có sẵn trong `Program.cs`). `install`/`clean logs` **không có CLI arg riêng** — phải giả lập gõ `1`/`3` vào menu tương tác qua `RedirectStandardInput` (xem `RunMenuChoiceAsync`).
- Thư mục game dùng chung với Multi-Client: `AowBoosterPage` tự mượn `GameFolder` đã lưu ở `MultiClientService.LoadToken()` nếu chưa cấu hình riêng.

## Token/context hygiene

`.claude/settings.json` đã deny Read cho `bin/`, `obj/`, `.vs/`, `Publish*/`, `.agents/`, và các binary lớn (`*.exe`, `*.dll`, `*.pdb`, `*.log`, `*.dat`, `*.zip`). Không cần đọc các thư mục/file này để hiểu codebase.
