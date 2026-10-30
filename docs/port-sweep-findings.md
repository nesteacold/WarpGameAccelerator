# Thí nghiệm: source port có điều khiển được colo / độ tới server game không?

Ngày: 2026-10-29, 17:22–17:42 (+07). Mạng: ISP Việt Nam. Không bật Boost, không có mihomo chạy.

## Giả thuyết

Lấy từ `cinit/udpfwd`: ECMP của Cloudflare hash trên **(src IP, src port, dst IP, dst port)**.
Vì dst cố định với WARP, **source port quyết định server WARP nào xử lý session** ⇒ kỳ vọng:
pin source port khác nhau → ra colo khác nhau (HKG/SIN) và/hoặc tỉ lệ lỗi khác nhau tới
`103.197.172.23:4000`. Nếu đúng, có thể (1) chọn port ra HKG, (2) giữ port để giữ colo qua
các lần reconnect.

## Dụng cụ

- `wireproxy 1.1.3` (pufferffish), `[Interface] ListenPort = N` để pin UDP source port, expose SOCKS5.
  Khoá lấy từ `%LocalAppData%\WarpGameAccelerator\Data\warp_account.json`.
- `usque 4.2.1` chạy MASQUE (RFC 9484) qua QUIC/UDP 443, config từ `warp_masque_account.json`.
- `curl` qua SOCKS5h. Tín hiệu: `colo`/`ip` từ `https://1.1.1.1/cdn-cgi/trace`;
  TCP connect tới `103.197.172.23:4000` (`rc=52` = nối được, `rc=28` = timeout, `rc=7` = SOCKS lỗi).
- Xác nhận pin hoạt động: `netstat -ano -p UDP` cho thấy đúng `0.0.0.0:<port>` đã chỉ định.

## Kết quả 1 — sweep 32 port (17:25–17:29), `experiments/port-sweep/results.csv`

| Chỉ số | Kết quả |
|---|---|
| Colo | **31/31 = SIN** — không port nào ra HKG |
| Egress IP | 5 IP khác nhau (104.28.210.150 ×25, .242.153 ×2, .210.154 ×2, .242.154 ×1, .210.153 ×1) |
| `game_rc=52` (nối được) | 28/31 |
| `game_rc=28` (timeout) | 3/31 |
| `rc=7` (SOCKS không lên) | 1/31 — **lỗi harness**, port 20002 bind lại bình thường khi chạy riêng |

**Kết luận 1:** hash ECMP theo 5-tuple là **có thật** (source port đổi → egress IP đổi), nhưng
**không đổi được colo**: mọi port đều ra SIN. Giả thuyết "pin port để chọn HKG" **sai** trên mạng này.
Khớp với kết luận cũ trong CLAUDE.md: mọi endpoint anycast đều về cùng một PoP.

## Kết quả 2 — cửa sổ lỗi mở lúc ~17:32

| Thời điểm | Thiết kế | Kết quả |
|---|---|---|
| 17:32–17:36 | 2 tunnel WG pin port 20000 + 20017, probe đan xen | A fail 19/20, B fail 17/20 — **fail cùng nhau** |
| 17:37–17:38 | 1 tunnel WG duy nhất (đối chứng) | **10/10 fail** ⇒ loại trừ giả thuyết "2 tunnel cùng key triệt tiêu nhau" |
| 17:39–17:42 | WG (wireproxy) vs MASQUE (usque) đan xen, 2 account khác nhau | **WG 11/12 fail, MASQUE 10/12 fail** |
| 19:50–19:53 | tunnel của mình (wireproxy) vs **`warp-svc` chính chủ** (`warp-cli mode proxy`, port 40000) | **ours 9/12 fail, warp-svc 12/12 fail** |

Colo trong cửa sổ lỗi vẫn SIN, `1.1.1.1/cdn-cgi/trace` **luôn thành công** (tunnel khoẻ), chỉ đường
tới `103.197.172.0/24` gãy — đúng hình dạng episode đã ghi trong CLAUDE.md.

**Kết luận 2:** lỗi **không phụ thuộc** vào:
- source port (4 port khác nhau đều fail),
- protocol (WireGuard/UDP 2408 và MASQUE/QUIC-UDP 443 đều fail),
- implementation (**wireproxy** dùng wireguard-go, **usque** là code MASQUE độc lập — không dính mihomo),
- account (2 device key khác nhau),
- **client chính chủ Cloudflare** (`warp-svc`, chế độ proxy mode, fail 12/12 trong cùng cửa sổ).

⇒ **Giả thuyết (b) "implementation userspace WireGuard của mihomo" bị bác bỏ**, và cả giả thuyết
"chỉ client chính chủ được ưu tiên" cũng bị bác bỏ. Lỗi nằm ở chặng **sau** Cloudflare
(Cloudflare → `103.197.172.0/24`) hoặc ở việc ISP xử lý UDP trong cửa sổ đó — không phải ở bất kỳ
client stack nào.

**Đối chiếu với phép đo 2026-08-22** (khi đó `warp-svc` 0/5 fail, mihomo 5/5 fail, dẫn tới giả
thuyết implementation): hai phép đo **mâu thuẫn** nhau. Phép đo mới có đối chứng mạnh hơn (nhiều
implementation, nhiều account, nhiều protocol, chạy đan xen trong cùng cửa sổ), nên kết luận
"không phải client stack" là kết luận đúng; phép đo cũ nhiều khả năng trùng hợp window ngắn hơn
so với thời gian warp-svc cần để thể hiện lỗi.

## Hệ quả cho roadmap

- **Bỏ** hướng thay mihomo bằng kernel WireGuard (`wireguard-nt`, MicroWARP): tiền đề của nó là
  giả thuyết (b), nay đã bị bác bỏ bằng đo.
- **Bỏ** hướng "pin source port để chọn colo": colo không đổi theo port trên mạng này.
- **Còn lại** đúng một hướng có cơ sở: thay đổi chặng *sau* Cloudflare — tức tự host relay/VPS
  (mô hình LightSpeed/lagvex) hoặc Zero Trust dedicated egress. Đây là thay đổi kiến trúc, đánh đổi
  bằng việc mất lợi thế "không cần hạ tầng".

## Giới hạn của phép đo

- Tín hiệu là TCP connect tới cổng 4000, không phải lưu lượng UDP thật của game. Cùng phương pháp
  với phép đo 49,4% trước đây nên so sánh được, nhưng không suy ra trực tiếp trải nghiệm trong game.
- Mỗi port chỉ 1 mẫu trong sweep; cửa sổ lỗi xuất hiện ngẫu nhiên nên tỉ lệ 3/31 **không** phải
  tỉ lệ lỗi thật.
- `warp-cli` đã được đổi sang `mode proxy` để đo rồi **khôi phục** về `WarpWithDnsOverHttps` +
  Disconnected. Cổng proxy 40000 vẫn còn set trong cấu hình (vô hại khi không ở proxy mode).
- Phép đo A3 dùng TCP connect cổng 4000 qua SOCKS của `warp-svc`; nếu `warp-svc` tiết lưu kết nối
  burst (như đã ghi ở mục "Probe burst qua SOCKS5" trong CLAUDE.md) thì con số 12/12 có thể bị
  khuếch đại. Tuy vậy 12/12 so với 3/12 của đường còn lại vẫn cho thấy không có lợi thế cho client
  chính chủ.

## File thô

`experiments/port-sweep/{results.csv, ab_results.csv, wg_vs_masque.csv, usque.log}`,
script tái lập: `sweep.sh`, `ab.sh`.
