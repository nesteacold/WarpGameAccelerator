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
- **KHÔNG dùng `DOMAIN-SUFFIX` để vá lỗi process-detection** — phá vỡ Split Tunneling, có thể đẩy traffic duyệt web (không phải game) vào tunnel WARP+ giới hạn băng thông. Nếu traffic rò ra ngoài tunnel, sửa `find-process-mode` hoặc bổ sung tiến trình còn thiếu; chỉ khi mihomo **không attribute được tiến trình nào** mới dùng `IP-CIDR` theo IP server game — xem mục "Rò rỉ traffic game" bên dưới.
- WireGuard block cần `keepalive: 25` (chống rớt UDP khi NAT timeout) và `udp: true`.
- `inet4-route-exclude-address` phải là **raw IP**, không phải hostname — mihomo crash fatal khi parse hostname. Chỉ set field này `if (IPAddress.TryParse(host, out _))`.
- `tun.inet6-address: []` (**danh sách rỗng, bắt buộc giữ**) — mặc định mihomo gán `fdfe:dcba:9876::1/126` cho TUN, `auto-route` kéo theo route `::/0` metric 0, thắng route IPv6 của NIC vật lý (metric 256) và hút toàn bộ IPv6 của cả máy vào tunnel. Nhưng outbound WARP/WireGuard chỉ có địa chỉ IPv4 → IPv6 rơi vào hố đen, app phải chờ timeout rồi mới fallback IPv4 (Happy Eyeballs) → "khựng nhẹ" khi duyệt web / Chrome Remote Desktop, dù game (server IPv4-only) vẫn chạy. Log nhận diện: nhiều dòng `dial DIRECT (match Match/) ... --> [xxxx:...]:443 error: i/o timeout` với đích là IPv6 literal, kèm `remotedesktop-pa`/`instantmessaging-pa.googleapis.com` (signaling của CRD). Sau khi để rỗng: 0 lỗi dial IPv6, IPv4 TCP retransmit 12.81% → 0%, IPv6 native đo được 39ms/0% loss.
- **KHÔNG set cứng `tun.interface-name` thay cho `auto-detect-interface: true`** — đã thử để sửa warning `[TUN] Auto detect interface ... get same name with tun`, kết quả **WireGuard handshake fail 100% mọi traffic** (`context deadline exceeded`, xác nhận bằng traffic test độc lập, không phải do game). Nguyên nhân: bind-socket-to-interface lỗi trên Windows ở tầng core Go — xem [mihomo#1728](https://github.com/MetaCubeX/mihomo/issues/1728). Bản thân warning đó gần như vô hại (chỉ 17/795 dòng log, routing table IPv4 không hề thay đổi khi đo thực nghiệm).
- DNS: `enhanced-mode: fake-ip`, range `198.18.0.1/16`, `dns-hijack: any:53` — bắt buộc để game resolve domain trước khi kết nối.

**Cách chẩn đoán "mạng giật/mất kết nối" cho đúng** (đã trả giá vì làm sai):
- Ping/ICMP qua TUN do mihomo **giả lập** — dùng nó để đo loss sẽ ra số sai. Log `receive ICMP echo reply ... i/o timeout` phần lớn là ICMP emulation, không phải mất gói thật; server game (`103.197.172.23`) còn rate-limit ICMP nên ping/loss app hiển thị không phản ánh chất lượng kết nối thật.
- Dùng bằng chứng từ bộ đếm OS: `netstat -s` (Segments Retransmitted theo IPv4/IPv6 riêng), `Get-NetAdapterStatistics` (discard/error theo adapter), và log của chính mihomo (phân loại lỗi theo outbound: `DIRECT` vs `WARP-Direct` — biết ngay lỗi ở tunnel hay ở đường thường).
- Cẩn thận harness đo: PowerShell 5.1 `New-Object Net.Sockets.TcpClient` không tham số tạo socket **chỉ IPv4** → mọi test tới địa chỉ IPv6 fail giả. Phải truyền `[Net.Sockets.AddressFamily]::InterNetworkV6`.
- **Đo UDP phải dùng socket BỀN VỮNG** (mở 1 lần, gửi nhiều lần). Tạo socket mới mỗi mẫu là đo **chi phí dựng session của mihomo** (NAT entry + tra tiến trình) chứ không phải chất lượng đường truyền — và luồng realtime thật (media CRD/WebRTC) dùng session sống lâu nên không trả chi phí đó. Đo thực nghiệm cùng đích, cùng thời điểm: socket-mới-mỗi-mẫu p95 72ms/max 610ms, socket-bền-vững p95 56ms/**max 65ms, 0 lần vượt 200ms**. Một probe 60 phút từng báo "60 spike" hoàn toàn vì lỗi này.
- **TCP connect time KHÔNG phải RTT mạng** khi TUN bật: mihomo hoàn tất handshake ở stack userspace rồi mới dial ra, nên connect tới đích qua tunnel lẫn đích DIRECT đều ra ~1-3ms dù RTT thật 37-50ms. Dùng ICMP/UDP để đo RTT.
- **KHÔNG xoá `Data\warp_account.json` để "làm mới" tài khoản** — `GetOrCreateAccountAsync()` chỉ re-apply WARP+ license nếu đọc được file cũ, xoá file là **mất luôn license key**, tài khoản mới tụt về Free. License thật còn bản sao trong `Data\warp_masque_account.json` (kiểm tra tier thật bằng `account_type` == `unlimited`, **không** dựa vào field `License` hay `warp_plus`).
- **Luôn đo bằng A/B có biến kiểm soát, đừng suy luận từ log rồi kết luận.** Trong một phiên điều tra đã có **4 giả thuyết nghe rất hợp lý nhưng đều sai** khi đem đo: (1) `interface-name` sửa được warning auto-detect → làm gãy handshake; (2) `auto-detect-interface` làm xáo trộn routing table → routing table IPv4 bất biến qua mọi mẫu đo; (3) DNS đi qua tunnel gây treo → DNS p50 chỉ 54.6ms, và spike xảy ra cả trên đường DIRECT; (4) mihomo TUN datapath gây treo → bật TUN mà không có tải game thì sạch hoàn toàn. Biến kiểm soát rẻ nhất và hiệu quả nhất: **ping `127.0.0.1` trong cùng vòng lặp đo** — nếu nó cũng chậm thì là process/OS bị bỏ đói CPU chứ không phải mạng (đo được: 0ms trên toàn bộ ~2500 mẫu, loại bỏ dứt điểm giả thuyết CPU).

## Rò rỉ traffic game ra ngoài tunnel (`dd.woniu.com`)

Triệu chứng: log mihomo có `dial DIRECT (match Match/) 198.18.0.1:6500 --> dd.woniu.com:80 error: ... i/o timeout` → traffic game đi ra ISP thay vì qua tunnel rồi timeout ~20s (server game chỉ vào được qua tunnel).

**Phân biệt 2 dạng bằng dấu ngoặc trong log** — quyết định cách vá:
- `198.18.0.1:6302(nvcontainer.exe) --> ...` — **có** tên tiến trình ⇒ mihomo nhận diện được, chỉ thiếu rule ⇒ vá bằng cách thêm tiến trình vào `GameProfileService`.
- `198.18.0.1:6500 --> ...` — **không có gì trong ngoặc** ⇒ mihomo không attribute được tiến trình nào ⇒ thêm `PROCESS-NAME` bao nhiêu cũng vô ích. Nguyên nhân: tiến trình sống rất ngắn (bật lên, mở kết nối, thoát ngay) nên khi mihomo tra bảng thì nó đã chết.

**Vá 2 lớp** (đang áp dụng):
1. `GameProfileService.cs` — profile Age of Wushu liệt kê **8** exe, không phải 3: ngoài `fxlaunch/fxupdate/fxgame` còn `gamefetchex.exe` (tải data, đích `dd.woniu.com`), `fxres.exe`, `bugreport.exe` (đích `crashlogs.mobilegame.woniu.com`), `iepop.exe` (browser nhúng), `SnailRes.exe`. **CỐ Ý KHÔNG thêm `ping.exe`** dù game có `bin\ping.exe`: `PROCESS-NAME` khớp theo TÊN nên sẽ bắt luôn `ping.exe` của Windows; muốn thêm phải dùng `PROCESS-PATH`.
2. `MihomoService.BuildGameServerIpRulesAsync()` — resolve `GameServerHosts` lúc sinh config rồi emit `IP-CIDR,<ip>/32,<proxy>`. Đây là lưới an toàn cho nhóm không attribute được. Dùng IP-CIDR **chứ không** DOMAIN-SUFFIX: IP của riêng server data thì trình duyệt không chạm tới, nên không phá Split Tunneling. Resolve lại mỗi lần Boost nên IP đổi vẫn tự cập nhật; host nào resolve lỗi thì bỏ qua, không chặn Boost.

## Kết quả điều tra "khựng nhẹ CRD + internet" (đã khắc phục)

Triệu chứng gốc: baseline mạng rất tốt nhưng có những cú treo **1-3.6 giây, ~1.7 lần/phút**, làm CRD và duyệt web khựng; game vẫn chạy.

Đã đo bằng 4 điều kiện để cô lập biến (mỗi dòng là một lần chạy probe riêng, ~9-15 phút):

| ĐK | TUN | game | CRD | spike |
|----|-----|------|-----|-------|
| A | ON | 5 client | có | **26 (1.7/phút)** |
| B | OFF | không | — | 0 |
| C | ON | không | — | 1 nhẹ |
| D | ON | không | **có** | **0** |

Loại trừ được: ISP/đường truyền (B sạch), mihomo–TUN tự nó (C+D có TUN 12.8 phút vẫn sạch), CRD (D sạch, upload chỉ 0.55 Mbps nên **không phải bufferbloat**), CPU starvation (`local_ctrl` 0ms), và tier WARP+ vs Free (treo xảy ra ở cả hai). Còn lại: **tải game qua mihomo**.

Sau khi áp `inet6-address: []` + vá rò rỉ woniu 2 lớp, chạy lại đúng điều kiện A: **0 spike / 1000 mẫu / 8.7 phút**, icmp p50 49ms (max 62ms), stun p50 36.9ms (max 129.6ms) — người dùng xác nhận CRD mượt. Tương quan đáng chú ý: số kết nối TCP giảm từ 193-378 xuống 68-168, khớp giả thuyết "rò rỉ → timeout 20s → retry dồn kết nối → mihomo tra bảng tiến trình trên bảng lớn → stall". **Chưa chứng minh được nhân quả** (hai lần đo khác nhau nhiều biến, và số kết nối được đếm bằng 2 phương pháp khác nhau) — nếu triệu chứng tái diễn, 2 đòn bẩy đã khoanh vùng nhưng **chưa** thử là `find-process-mode: always → strict` và `tun.stack: mixed → system`, thử từng cái một.

**Chưa sạch 100%.** Ping dài `1.1.1.1` qua 3 mốc: trước fix 284 gói/**12% loss**/avg 248ms → sau fix IPv6 2867 gói/0.87%/avg 83ms → sau fix woniu 4181 gói/**0.12%**/avg 56ms (min 48ms, tức đuôi spike gần phẳng). Nhưng `max` vẫn 3325ms ⇒ **vẫn còn cú treo nhiều giây, tần suất cỡ 1 lần/giờ** — thưa đến mức probe 9 phút không bắt được. Muốn điều tra tiếp thì phải đo liên tục hàng giờ rồi mới đối chiếu, đừng kết luận từ cửa sổ ngắn.

## WARP Account (`WarpAccountService.cs`)

- **Tài khoản WARP tự đăng ký qua HTTP API thường bị Cloudflare gán chính sách MASQUE-only** — mihomo không hỗ trợ MASQUE → Direct Mode không bao giờ handshake được dù config đúng.
- **Fix**: đăng ký qua `wgcf.exe` (embedded resource, extract ra `Core\wgcf.exe`) — tài khoản wgcf giữ chính sách WireGuard cổ điển. `GetOrCreateAccountAsync()` ưu tiên `RegisterViaWgcfAsync()`, fallback `RegisterNewWarpAccountAsync()` (raw API) nếu wgcf lỗi.
- Raw API fallback: version string đúng là `v0a1922` (không phải version cũ `v0i...`), User-Agent `okhttp/3.12.1`, `type: Android`. Sau khi register phải PATCH `warp_enabled: true`, nếu không handshake sẽ im lặng thất bại.
- WARP+ license: `PUT /v0a.../reg/{id}/account` với Bearer token; re-register vẫn tự re-apply license cũ — **nhưng chỉ khi `warp_account.json` còn đọc được** (code lưu `oldLicense` từ file cũ trước khi tạo mới). Xoá file = mất license, tài khoản mới về Free.

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
