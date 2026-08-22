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
  **Đã thử `strict` 2 lần, revert cả 2 — đừng thử lần thứ ba.** Lần 1: trong vụ Hyper-V, không giảm treo. Lần 2 (2026-08-21): thử `strict` **kèm** rule `IP-CIDR` cho dải world server đặt trước các `PROCESS-NAME`, giả thuyết là `always` phải tra bảng TCP cho **mọi** kết nối nên chi phí tăng theo số kết nối đang mở (5 client giữ hàng trăm) làm dial hết deadline. Kết quả: **không cải thiện**, người dùng vẫn mất kết nối. Lần này `strict` **không** gây rò rỉ (chỉ 6 dòng `match Match/`, đều là `chrome.exe`), nhưng cũng không được gì. Ghi chú thêm: deadline dial của mihomo **không cấu hình được** — binary v1.19.29 không có khoá `connect-timeout`/`tcp-connect-timeout`/`dial-timeout`, nó là hằng số biên dịch trong Go.
- **KHÔNG dùng `DOMAIN-SUFFIX` để vá lỗi process-detection** — phá vỡ Split Tunneling, có thể đẩy traffic duyệt web (không phải game) vào tunnel WARP+ giới hạn băng thông. Nếu traffic rò ra ngoài tunnel, sửa `find-process-mode` hoặc bổ sung tiến trình còn thiếu; chỉ khi mihomo **không attribute được tiến trình nào** mới dùng `IP-CIDR` theo IP server game — xem mục "Rò rỉ traffic game" bên dưới.
- WireGuard block cần `keepalive: 25` (chống rớt UDP khi NAT timeout) và `udp: true`.
- `inet4-route-exclude-address` phải là **raw IP**, không phải hostname — mihomo crash fatal khi parse hostname. Chỉ set field này `if (IPAddress.TryParse(host, out _))`.
- `tun.inet6-address: []` (**danh sách rỗng, bắt buộc giữ**) — mặc định mihomo gán `fdfe:dcba:9876::1/126` cho TUN, `auto-route` kéo theo route `::/0` metric 0, thắng route IPv6 của NIC vật lý (metric 256) và hút toàn bộ IPv6 của cả máy vào tunnel. Nhưng outbound WARP/WireGuard chỉ có địa chỉ IPv4 → IPv6 rơi vào hố đen, app phải chờ timeout rồi mới fallback IPv4 (Happy Eyeballs) → "khựng nhẹ" khi duyệt web / Chrome Remote Desktop, dù game (server IPv4-only) vẫn chạy. Log nhận diện: nhiều dòng `dial DIRECT (match Match/) ... --> [xxxx:...]:443 error: i/o timeout` với đích là IPv6 literal, kèm `remotedesktop-pa`/`instantmessaging-pa.googleapis.com` (signaling của CRD). Sau khi để rỗng: 0 lỗi dial IPv6, IPv4 TCP retransmit 12.81% → 0%, IPv6 native đo được 39ms/0% loss.
- **KHÔNG set cứng `tun.interface-name` thay cho `auto-detect-interface: true`** — đã thử để sửa warning `[TUN] Auto detect interface ... get same name with tun`, kết quả **WireGuard handshake fail 100% mọi traffic** (`context deadline exceeded`, xác nhận bằng traffic test độc lập, không phải do game). Nguyên nhân: bind-socket-to-interface lỗi trên Windows ở tầng core Go — xem [mihomo#1728](https://github.com/MetaCubeX/mihomo/issues/1728). Bản thân warning đó gần như vô hại (chỉ 17/795 dòng log, routing table IPv4 không hề thay đổi khi đo thực nghiệm).
- DNS: `enhanced-mode: redir-host` (**KHÔNG dùng `fake-ip`**), `dns.ipv6: false`, `dns-hijack: any:53`.
  **Fake-IP đã bị bỏ hẳn có chủ đích** ở commit `fda8700` (2026-07-24) vì nó gây **lỗi cURL/TLS trong addon game** — cùng họ vấn đề với `tun.stack: gvisor` ở trên. Trước đó `afbe641` đã thử vá bằng `fake-ip-filter` cho domain VN + `skip-cert-verify` nhưng không đủ. Đừng "sửa lại cho giống tài liệu mihomo" — sẽ làm gãy addon game.
  Hai điều dễ nhầm: (a) dải `198.18.0.1` thấy trong log **không phải fake-ip** mà là `inet4-address` của chính card TUN, nên dòng `198.18.0.1:<port> --> ...` dùng để nhận diện nguồn kết nối vẫn đọc đúng; (b) `redir-host` phân giải tên ra **IP thật trước** rồi mới khớp rule, nên rule `IP-CIDR` áp được cả cho kết nối theo domain — khác `fake-ip`, nơi domain mang IP giả nên `IP-CIDR` không khớp.

**Cách chẩn đoán "mạng giật/mất kết nối" cho đúng** (đã trả giá vì làm sai):
- Ping/ICMP qua TUN do mihomo **giả lập** — dùng nó để đo loss sẽ ra số sai. Log `receive ICMP echo reply ... i/o timeout` phần lớn là ICMP emulation, không phải mất gói thật; server game (`103.197.172.23`) còn rate-limit ICMP nên ping/loss app hiển thị không phản ánh chất lượng kết nối thật.
- **Đếm dòng `error` trong log mihomo mà không phân loại là sai.** Log trộn hai thứ khác hẳn nhau: `[TCP] dial ... error: context deadline exceeded` (dial **thật** thất bại) và `receive ICMP echo reply: read ip4 ... i/o timeout` (**ICMP giả lập** của mihomo). Server game `103.197.172.x` **chặn ICMP** nên loại thứ hai luôn timeout và là **nhiễu dự kiến, không phải mất kết nối** — nó xuất hiện đều đặn vài phút một lần (khả năng cao do chính client game ping để hiện latency). Bộ lọc phải yêu cầu `] dial` mới đếm. Đã trả giá: trong một phiên có **3 báo cáo sai liên tiếp** vì gộp ICMP vào "lỗi dial của game", suýt kết luận ngược về hiệu quả của một thay đổi config.
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

- **Tài khoản WARP tự đăng ký qua HTTP API thường bị Cloudflare gán chính sách MASQUE-only** — tài khoản đó không dùng được cho outbound `type: wireguard`, nên Direct WireGuard không bao giờ handshake được dù config đúng.
  **ĐÍNH CHÍNH (2026-08-22): mihomo CÓ hỗ trợ MASQUE.** Binary v1.19.29 chứa `masque`, và app có sẵn engine mode `DirectMasqueBeta` (từ v1.13.0, bật trong Dev Panel) sinh outbound `type: masque` + `sni` + port 443, dùng tài khoản riêng `warp_masque_account.json`. Câu "mihomo không hỗ trợ MASQUE" trong tài liệu cũ là **sai** và đã khiến bỏ qua chính phép thử đáng làm nhất (xem mục "Ba engine mode" bên dưới).
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

## Hyper-V xung đột với TUN (nguyên nhân gốc của "ping timeout + rớt client + giật CRD")

**Triệu chứng**: khi Boost bật, ping tới đích đi qua tunnel timeout liên tục (đo được **12.1%** — 290/2400 mẫu, spike tới **3.5 giây**), client game rớt lẻ tẻ, Chrome Remote Desktop giật/đứt. Tắt Boost thì **hết sạch ngay**. mihomo **không** tốn CPU lúc treo (0-109ms), log không có lỗi nào ngoài `context deadline exceeded` — tức nó đang *chờ*, không phải quá tải.

**Nguyên nhân — chính xác là NDIS filter của virtual switch, KHÔNG phải hypervisor**: sau khi khắc phục, đo lại thấy `HypervisorPresent: True` và `VirtualizationBasedSecurityStatus: 2` (**hypervisor vẫn đang chạy**) mà mạng đã hết lỗi hoàn toàn. Thứ đổi trạng thái là binding **`vms_pp` (Hyper-V Extensible Virtual Switch)** trên card vật lý → `Enabled: False`. Vậy đừng đi tắt hypervisor; hãy nhắm vào binding này. Cùng họ vấn đề với `WireGuardTunnel$*` ở mục trên: hai driver ảo chồng lên nhau trong datapath.

**Cách xử lý** (không sửa được từ code app — là môi trường máy). Kiểm tra binding trước:
```
Get-NetAdapterBinding -Name "<NIC>" | Where-Object ComponentID -match 'vms|hyper'
```
Nếu `vms_pp` đang `Enabled: True` → bỏ binding đó khỏi **card vật lý** (giữ được VM Hyper-V, miễn VM không cần nối mạng ngoài qua đúng card đó). Cách mạnh tay hơn (đã dùng lần này): gỡ tính năng Hyper-V. Trước khi gỡ, kiểm tra không phá thứ khác: `Get-VM`, `wsl -l -v`, Docker, và **Memory Integrity/HVCI** (`Get-CimInstance Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard` → `SecurityServicesRunning` phải là 0 mới an toàn).

**Cơ chế chỉ ở mức giả thuyết** — traffic qua mihomo đi đường dài hơn (`app → wintun → mihomo userspace → socket mới → stack → NDIS filter → NIC`), tức bị **tái tiêm** vào stack nên qua tầng WFP/NDIS hai lần, và đó là nơi gặp filter Hyper-V. **Nghịch lý chưa giải thích được**: traffic bypass (`8.8.8.8`) cũng qua đúng card và đúng filter đó nhưng 0 lỗi trên 2400 mẫu ⇒ không phải "filter làm chậm card". Muốn biết chính xác (nghẽn hàng đợi? tranh chấp lock? DPC trễ?) phải truy vết kernel bằng ETW/NDIS trace — chưa làm.

**Cách đo đã phá được ca này** (quan trọng hơn kết luận): thêm **một IP public vào `inet4-route-exclude-address`** (đang dùng `8.8.8.8/32`, xem `DiagnosticBypassIps` trong `MihomoService.cs`) để có **đường đối chứng thật không đi qua mihomo**. Rồi ping song song 2 đích:
- `8.8.8.8` (bypass) — suốt 2400 mẫu: **0 lỗi**, 30-31ms, jitter 0.4ms
- `1.1.1.1` (qua TUN) — **290 lỗi**

100% sự kiện đều là "chỉ đường qua mihomo lỗi", 0 lần cả hai cùng lỗi ⇒ loại dứt điểm ISP/uplink/CPU chỉ bằng một phép đo. Thêm `ping 127.0.0.1` cùng vòng lặp để loại CPU starvation (đo được 0ms trên mọi mẫu).

**Những thứ KHÔNG phải nguyên nhân** (đã đo, đừng thử lại):
- Dải port bị `winnat` đặt trước (~800-1700 port UDP): sau reboot chúng **quay lại** mà mạng vẫn bình thường ⇒ không phải nguyên nhân. Nhưng chúng **có** gây lỗi thật: `Start DNS server(TCP) error: listen tcp 0.0.0.0:1053: bind: ... forbidden by its access permissions` vì port 1053 nằm trong dải TCP `1024-1123` Hyper-V chiếm. Tắt Hyper-V thì lỗi này tự khỏi — **không cần đổi port DNS**.
- `find-process-mode: strict`: đã thử để giảm treo, **không có tác dụng** (tunnel vẫn `context deadline exceeded`), đã revert về `always`.
- WARP+ vs Free, anycast đổi edge (egress IP bất biến 1h40), CRD, bufferbloat (upload chỉ 0.55 Mbps), IPv6, tập ICE candidate — tất cả đã loại bằng đo.

**Bẫy phụ phát hiện cùng lúc**: app **không có cơ chế chặn đa instance**. Mở 2 cửa sổ app → mỗi instance khi khởi động đều gọi `StopProxy()` (trong `ExtractCoreResources()`) nên **kill mihomo của instance kia**, làm rớt toàn bộ client. Dấu hiệu nhận biết trong `trace.log`: `RegisterHotKey thất bại — tổ hợp phím có thể đã bị chiếm`. Nếu điều tra "tự nhiên rớt client", **đếm số tiến trình `WarpGameAccelerator` trước tiên**.

## Chỉ số hiển thị: KHÔNG được bịa (đã trả giá)

Tính năng "chọn node + đo ping" (thêm ở v1.9.0, commit `b080f49`) từng hiển thị **số liệu ảo**, và nó làm mù công cụ chẩn đoán trong nhiều tháng. Đã sửa; ghi lại để không tái diễn.

**Cái gì đã sai:**
- `CloudflareNodeService.PingNodeAsync` lấy RTT ICMP thật rồi **cộng hằng số cứng theo nhãn node** (`tw01 +32`, `tw02 +35`, `hkg01 +42`, `sin01 +52`...) **cộng `Random.Shared.Next(0,3)`**; nhánh `catch` trả về số bịa hoàn toàn (`36/47/58 + Random`). Node `auto` trả cứng `35`, không đo gì. Hệ quả: thứ tự "Taiwan tốt hơn HK tốt hơn SIN" là do hằng số viết cứng, không phải kết quả đo — trong khi đo thật cả 4 endpoint đều **48-50ms** vì đều là anycast và **cùng về một PoP**.
- `PingMonitorService.MeasurePingAsync` gọi `PingNodeAsync` **trước tiên** rồi `if (nodePing > 0) return`. Hàm đó luôn > 0 ⇒ nhánh đo thật bên dưới **không bao giờ chạy** ⇒ ô PING trên Dashboard **không liên quan gì tới server game**.
- `PacketLossPercent = ping < 0 ? 100.0 : 0.0` — suy ra từ **một** mẫu, mà mẫu đó luôn > 0 ⇒ ô LOSS **luôn hiện 0,0%**.

**Vì sao nó tốn kém:** đây chính là lời giải cho triệu chứng "ping/loss trong app vẫn báo bình thường nhưng thực tế mất kết nối" ghi ở các mục dưới. Không phải ping đo sai — **nó không đo gì cả**. Đo thực nghiệm: 49% kết nối tới server game thất bại trong khi ô PING hiển thị ~84ms đều đặn.

**Quy tắc từ nay:**
- **`Random` không được xuất hiện trên bất kỳ đường hiển thị số liệu nào.** Cần jitter giả để "trông thật" tức là số liệu không có thật.
- Không đo được thì **hiện "không đo được"** (`-1`), tuyệt đối không nội suy và không lấy số của đại lượng khác thay thế.
- Nhãn phải nói đúng đại lượng: ô ping hiện là **"PING (EDGE WARP)"** kèm chú thích "RTT tới edge — KHÔNG phải ping server game".

**RTT tới server game là KHÔNG đo được từ client khi TUN bật** (đã kiểm chứng cả 3 đường): ICMP bị mihomo giả lập; TCP-connect hoàn tất ở userspace (**đo được 0.0016s** cho mọi đích, kể cả đích không tới được — nên `TcpTestSucceeded=True` KHÔNG chứng minh tới được); server game cổng 4000 **không gửi byte nào** khi vừa kết nối nên cũng không đo được qua `time_starttransfer`. Thay vào đó dùng **log dial của mihomo làm nguồn sự thật** (`MihomoService.LastGameDialFailureUtc`) — mihomo chỉ log thất bại, nên tín hiệu là "có lỗi dial gần đây", **không phải** "tunnel đã chết" (một client retry cũng sinh lỗi).

Thêm: RTT tới edge **chỉ đáng tin ở chế độ Direct WireGuard**, vì khi đó endpoint được đưa vào `inet4-route-exclude-address` nên ICMP đi thẳng ra NIC. Ở WARP Client Proxy không có endpoint nào được loại trừ nên ICMP đi qua TUN và bị giả lập — `DashboardViewModel` cố ý hiện "không đo được" trong trường hợp đó.

## Chọn node: anycast KHÔNG chọn được vùng địa lý

Danh sách node trong `CloudflareNodeService.GetDefaultNodes()` chỉ là các **IP anycast** Cloudflare (`162.159.192.1`, `.193.1`, `.195.1`, `188.114.96.1`). Anycast nghĩa là cùng một IP được quảng bá từ mọi PoP; **PoP nào trả lời do BGP của ISP quyết định**, client không chọn được. WARP consumer không có tham số chọn colo.

Đã kiểm chứng (ISP Việt Nam, 2026-08-21): RTT tới cả 4 IP node = 50/48/49/49ms (như nhau, tức cùng PoP); `colo=SIN` khi query qua `1.1.1.0/24`, `1.0.0.0/24`, `104.16/12`, qua IPv6 của ISP, **và** qua WARP gốc tự chọn endpoint. Node `auto` và `vn_hcm_tw01` **dùng đúng cùng một IP** nên giống nhau từng byte. Vậy các nhãn "Taiwan (Taipei 01/02/03)", "Hong Kong", "Cáp nội địa HCM ➔ Cloudflare Backbone ➔ Taiwan" là **chữ trang trí**, không có cơ chế phía sau.

**Nhưng cùng colo KHÔNG có nghĩa cùng đường đi.** Muốn đổi vùng thật thì cần thứ ngoài WARP consumer (Zero Trust dedicated egress — chưa kiểm chứng, hoặc relay qua VPS ở vùng đích).

## Harness đo mạng qua tunnel (dùng lại được)

Cách đo đã phá được nhiều ca trong dự án này, chi phí thấp:

1. **Probe đi qua tunnel:** copy `C:\Windows\System32\curl.exe` thành tên khớp một rule `PROCESS-NAME` (dùng `SnailRes.exe` — **đừng** dùng `fxgame.exe` vì `MultiClientService` đếm client theo tên đó). Probe bằng `curl.exe` gốc (tên không có trong rule) sẽ khớp `MATCH,DIRECT`, cho ngay **cặp A/B cùng đích, khác outbound**.
2. **Phán quyết bằng log mihomo**, không bằng exit code hay `TcpTestSucceeded`: sau probe, đọc các dòng log mới; có dòng chứa tên tiến trình probe + đích + `error` là dial thất bại; không có dòng nào là dial OK.
3. **Đối chứng bắt buộc:** một đích trung lập qua tunnel (`1.1.1.1` đã có rule sẵn), `https://1.1.1.1/cdn-cgi/trace` để lấy egress IP/colo/tier thật, và đích game qua DIRECT. Không có đối chứng thì mọi kết luận đều lung lay.
4. **A/B/A bắt buộc khi so sánh cấu hình:** lỗi ở đây flapping theo cửa sổ nhiều phút, nên đo A rồi B rồi kết luận là **sai** — phải quay lại A.

**Bẫy harness đã gặp:** (a) PowerShell `$matches` trùng biến tự động của toán tử `-match`, phải dùng tên khác; (b) `Get-CimInstance Win32_Process | Where CommandLine -like '*abc*'` **khớp chính tiến trình đang chạy lệnh đó** vì chuỗi tìm kiếm nằm trong command line của nó — phải loại trừ `$PID`; (c) script chạy qua `Start-Process -WindowStyle Hidden` bị treo ngay probe đầu (nghi AV chặn binary đổi tên ở tiến trình detached), chạy trong ngữ cảnh tool thì bình thường; (d) exit code 28 của `curl` là timeout đúng thiết kế, đừng đọc thành "phép đo thất bại"; (e) hai phiên census cùng ghi một file CSV/TXT sẽ đọc lẫn kết quả của nhau — mỗi phiên phải ghi ra file riêng.

## Sự cố `103.197.172.0/24` không tới được qua tunnel (chưa khắc phục)

Triệu chứng người dùng: client đứng yên trong map thì bình thường, nhưng thoát/vào lại liên tục thì rớt về màn hình đăng nhập; nhiều client cùng login thì chỉ một vào được.

Đo bằng harness trên, 178 chu kỳ mỗi 30s (2026-08-21, 11:46-13:16):

| Đường & đích | Tỉ lệ thất bại |
|---|---|
| TUNNEL → `103.197.172.23:4000` | **49,4%** |
| TUNNEL → `103.197.172.29:4000` | **49,4%** |
| TUNNEL → `115.182.197.210:80` (đối chứng) | 0,0% |
| DIRECT → cả hai IP game | 0,0% |

Hình dạng: **bật/tắt theo cửa sổ nhiều phút** (15 cửa sổ lỗi, trung bình 188s, dài nhất **600s**), `.23` và `.29` cùng trạng thái ở 93% chu kỳ nên đây là lỗi chung cho cả đường tới `/24`, không phải từng kết nối riêng lẻ. Tunnel vẫn khỏe suốt (HTTP end-to-end 178/178 OK).

**Mức độ thay đổi theo thời gian trong ngày** — đo lại cùng ngày lúc 17:06-17:50 chỉ còn 7-14%. Vì vậy **mọi so sánh cấu hình phải A/B/A**, đừng kết luận từ hai phép đo ở hai thời điểm.

**Đã loại trừ bằng đo:** server chết (DIRECT 0% lỗi), ISP (đối chứng sạch), socket/tunnel chết (HTTP qua tunnel luôn OK; restart mihomo **không** cứu, lỗi lại ngay sau 18s), tải client (nhiều cửa sổ lỗi có `fxfail=0`, tức không client nào đang dial), đổi engine mode (A/B/A cho thấy khác biệt là do thời điểm), đổi node/egress (không đổi được colo).

**Giải thích khớp mọi quan sát:** đây là điều kiện theo cửa sổ trên đường egress-WARP tới `/24` đó. "Burst" trong log **không phải sự kiện mạng** mà là **sự kiện client retry** trùng vào cửa sổ lỗi. Client đứng yên dùng kết nối đã thiết lập nên không thấy gì; client vào lại phải dial mới nên đụng ngay.

**ĐANG BẬT (từ 2026-08-22):** rule `IP-CIDR,103.197.172.0/24,WARP-Direct` — hằng số `GameWorldServerCidr` trong `MihomoService`, phát ra **trước** mọi `PROCESS-NAME`. Đích là **proxy chứ không phải DIRECT** (xem đoạn dưới: IP Việt Nam bị chặn ở tầng ứng dụng). Giữ lại theo yêu cầu người dùng sau khi revert `strict`; về routing thì vô hại và vẫn là lưới an toàn khi không attribute được tiến trình.
**Đánh đổi (ĐÃ ĐÍNH CHÍNH):** ban đầu tưởng rule IP-CIDR làm log mất tên tiến trình — **sai**. Đo được dòng `dial WARP-Masque (match IPCIDR/103.197.172.0/24) 198.18.0.1:1622(fxgame.exe)` ⇒ attribution **vẫn có** khi rule khớp bằng IP-CIDR. Tên tiến trình xuất hiện khi mihomo **tra được** tiến trình, **không phụ thuộc rule nào khớp** (với `find-process-mode: always` thì nó tra cho mọi kết nối). Probe bằng `curl -Z` thường không tra được vì tiến trình quá ngắn. Muốn phân biệt probe của mình với traffic game thì buộc probe vào **dải cổng nguồn riêng** (`curl --local-port 45000-45999`) rồi lọc log theo `198.18.0.1:<port>` — mihomo có ghi cổng nguồn.

**Quan trọng — đường DIRECT KHÔNG dùng được để chơi:** probe TCP tới `:4000` qua DIRECT thành công 0 lỗi/590 mẫu, **nhưng** đó chỉ chứng minh TCP tới được cổng, KHÔNG chứng minh chơi được: **IP Việt Nam bị chặn ở tầng ứng dụng** (thông tin vận hành từ người dùng — bắt buộc phải có VPN mới đăng nhập được). Server chặn im lặng (drop, không RST) nên probe không phân biệt được hai chuyện đó. Vì vậy **đừng** đề xuất định tuyến dải game ra `DIRECT`.

**Đòn bẩy chưa dùng (giữ lại để tham khảo):** định tuyến `IP-CIDR,103.197.172.0/24,DIRECT`. DIRECT thắng tuyệt đối về độ tin cậy (0 lỗi trên toàn bộ mẫu) nhưng **đánh đổi độ trễ chưa đo được** (xem mục chỉ số ở trên: RTT tới server game không đo được từ client). Nếu làm: rule phải đặt **TRƯỚC** các dòng `PROCESS-NAME,...,WARP-Direct` vì mihomo lấy rule khớp đầu tiên. Lưu ý ghi chú "server game chỉ vào được qua tunnel" ở mục rò rỉ traffic **không đúng với world server**: DIRECT tới `:4000` thành công trên mọi mẫu đã đo.

**KHÔNG dùng proxy-group `fallback` của mihomo cho ca này:** health-check của nó đi bằng HTTP URL, mà HTTP qua tunnel vẫn OK ngay trong lúc episode xảy ra, nên group sẽ không bao giờ chuyển.

## Ba engine mode: chỉ khác nhau ở LỚP VẬN CHUYỂN (2026-08-22)

Cả ba mode **dùng chung** toàn bộ phần trên: mihomo TUN là cửa vào, **cùng bộ rules** (chỉ khác tên outbound), cùng DNS (`redir-host`, `ipv6: false`, `dns-hijack`), và **mihomo vẫn là bên tra tiến trình, khớp rule, rồi tự dial với deadline nội bộ**. Egress đo được cũng như nhau (`104.28.210.150` / colo `SIN`).

| Mode | Ai dựng tunnel | Giao thức / cổng | Cần WARP gốc? | Chữ ký lỗi đặc trưng |
|---|---|---|---|---|
| `DirectWireGuard` | mihomo | WireGuard / UDP **2408** | Không | `context deadline exceeded` |
| `DirectMasqueBeta` | mihomo | MASQUE-QUIC / UDP **443** | Không | `http3: PROTOCOL_VIOLATION (remote)` |
| `WarpClientProxy` | **warp-svc** | MASQUE-QUIC / UDP 443 | **Có** | ít lỗi, rải rác |

So hàng 1↔2 tách được yếu tố **giao thức/cổng** (cùng implementation). So hàng 2↔3 tách được yếu tố **implementation** (cùng giao thức). Đây là ma trận duy nhất tách được hai biến đó — dùng lại khi cần.

Chi tiết khác biệt đáng nhớ: deadline dial của mihomo áp lên **hai việc khác nhau** — ở Direct WireGuard nó phải hoàn tất *handshake WireGuard + bắt tay TCP xuyên tunnel* trong deadline; ở WarpClientProxy nó chỉ chờ *SOCKS negotiation với localhost*. Đường thứ nhất dễ vượt deadline hơn hẳn khi có mất gói. **Deadline đó KHÔNG cấu hình được** (v1.19.29 không có `connect-timeout`/`tcp-connect-timeout`/`dial-timeout`; là hằng số biên dịch Go).

**Nghịch lý cần nhớ:** `WarpClientProxy` có **nhiều lớp hơn** (thêm chặng SOCKS5 + một tiến trình) mà lại ổn định hơn trong cửa sổ lỗi. Nên "ít lớp thì tốt hơn" — cơ sở của nhãn "Direct WireGuard 🔥 khuyên dùng" — **không có bằng chứng**.

## Bẫy khi đánh giá mode nào tốt hơn (đã trả giá nhiều lần trong 1 phiên)

- **Ngoài cửa sổ lỗi thì MỌI mode đều sạch.** Direct WireGuard từng sạch **4h50 liền** (0 lỗi), rồi 18 phút sau lỗi 85 lần. Nên "chạy X phút không lỗi" **không** chứng minh mode tốt hơn. Chỉ so sánh **trong cửa sổ lỗi** mới có nghĩa.
- **Một dòng log lỗi ≠ mất kết nối.** Client retry và vào được thì người dùng không thấy gì. Đại lượng đúng là **episode**: cụm lỗi liên tiếp (gộp khi cách nhau <60s) và **độ dài episode**, vì chỉ episode đủ dài mới làm gãy một lần login/vào liên server. Đã một lần dựng cả kết luận sai từ **một** dòng `PROTOCOL_VIOLATION`.
- **Probe burst qua SOCKS5 KHÔNG đại diện cho game.** Bắn 4-16 kết nối *đồng thời tới cùng một đích* qua `warp-svc` bị nó tiết lưu: probe lỗi **114 lần** trong khi client game chỉ **11 lần** cùng khoảng thời gian (gấp ~10×). Tệ hơn, probe còn **thêm tải cho warp-svc** — tức tự làm xấu đường của người dùng trong lúc đo. Ở mode này phải đo **thụ động** bằng dial của chính game.
- **Restart mihomo XOÁ SẠCH `mihomo_runtime.log`.** Đã mất trắng 6 tiếng dữ liệu buổi sáng vì đổi mode. **Snapshot log ra chỗ khác trước mỗi lần restart/đổi mode.**

## Harness A/B "cùng khoảnh khắc" — tách mihomo khỏi đường sau mihomo

Suốt 2 ngày mọi phép đo đều gộp hai chặng (`PC→Cloudflare` và `Cloudflare→server game`) nên không biết chặng nào gãy. Cách tách:

1. Bật WARP gốc ở **proxy mode** (`warp-cli mode proxy` + `proxy port 40000` + `connect`) — nó **không tạo adapter** nên không tunnel toàn máy, chạy song song với Boost được.
2. **Probe A** đi qua rule mihomo (mihomo tự dial). **Probe B**: `curl --proxy socks5h://127.0.0.1:40000` — vì `127.0.0.0/8` là `DIRECT` trong rules nên B **không qua dial của mihomo**; `warp-svc` tự mở socket bằng stack OS. Cả hai ra **cùng egress** ⇒ biến duy nhất là ai dựng tunnel/ai dial.
3. **Hiệu chuẩn bắt buộc** trước khi tin Probe B: `rc=97` = SOCKS không nối được (**thất bại**); `rc=28` hoặc `rc=0` = **đã nối được**. Kiểm bằng một IP chết (`203.0.113.1`, TEST-NET) và một IP tốt.
4. Bắn A và B **đan xen dày** (B→A→B nhiều vòng mỗi chu kỳ) — cửa sổ lỗi chập chờn theo từng chục giây nên mẫu thưa cho kết quả trái ngược nhau.

**Kết quả đo được (2026-08-22 12:48, đang có cửa sổ lỗi, 4 client mất kết nối):** A qua mihomo/WireGuard **5/5 thất bại**, B qua warp-svc **0/5**. Đổi sang `WarpClientProxy` thì client vào lại được ngay. ⇒ Chặng `Cloudflare→server game` vẫn tốt; chỗ gãy là **cách mihomo dựng tunnel/dial**.

**Chưa chứng minh:** *vì sao* chặng đó gãy. Ba ứng viên còn nguyên: (a) cổng UDP 2408 bị tiết lưu (443 trông như HTTPS nên không bị), (b) implementation WireGuard userspace của mihomo, (c) Cloudflare đối xử khác với tài khoản đăng ký qua wgcf. Phép thử tách được: chạy `DirectMasqueBeta` (cùng implementation, khác giao thức/cổng) **trong cửa sổ lỗi** — chưa gặp cửa sổ nào tính tới 14:04.

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
