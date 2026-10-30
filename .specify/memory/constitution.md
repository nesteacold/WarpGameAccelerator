# WarpGameAccelerator Constitution

Hiến pháp của dự án: các ràng buộc **bất biến**, rút ra từ những lần đã trả giá thực nghiệm.
Mọi spec, plan, task và code review phải tuân theo tài liệu này. Chi tiết cách build/chạy và
bẫy cụ thể nằm ở `CLAUDE.md` — file đó **không** được mâu thuẫn với hiến pháp này.

## Core Principles

### I. Bằng chứng đo được đứng trên suy luận (NON-NEGOTIABLE)

Một thay đổi chỉ được coi là "tốt hơn" khi có phép đo A/B có biến kiểm soát. Không được kết luận
từ log, từ suy luận "nghe hợp lý", hay từ một lần chạy sạch.

- **A/B/A bắt buộc** khi so sánh cấu hình: lỗi ở dự án này flapping theo cửa sổ nhiều phút, nên
  đo A → B → kết luận là sai; phải quay lại A.
- **Ngoài cửa sổ lỗi thì mọi phương án đều sạch.** "Chạy X phút không lỗi" không chứng minh gì;
  chỉ so sánh **trong** cửa sổ lỗi mới có nghĩa.
- **Một dòng log lỗi ≠ mất kết nối.** Đại lượng đúng là *episode* (cụm lỗi cách nhau < 60s) và
  độ dài episode.
- **Đối chứng bắt buộc** trong mọi phép đo: một đích trung lập qua tunnel, một đích qua DIRECT,
  và `ping 127.0.0.1` cùng vòng lặp để loại trừ đói CPU.
- **Phân loại log trước khi đếm.** `] dial ... error` là dial thật thất bại; `receive ICMP echo
  reply ... i/o timeout` là ICMP giả lập của mihomo và là nhiễu dự kiến.
- **Backup dữ liệu đo trước khi restart mihomo** — restart xoá sạch `mihomo_runtime.log`.

**Lý do:** một phiên điều tra có 4 giả thuyết nghe rất hợp lý nhưng đều sai khi đem đo; một phiên
khác có 3 báo cáo sai liên tiếp vì gộp log ICMP giả lập vào "lỗi dial của game".

### II. Số liệu hiển thị phải là số đo thật (NON-NEGOTIABLE)

- **`Random` không được xuất hiện trên bất kỳ đường hiển thị số liệu nào.** Cần jitter giả để
  "trông thật" nghĩa là số liệu không có thật.
- Không đo được thì **hiển thị "không đo được" (`-1`)**. Tuyệt đối không nội suy, không lấy số
  của đại lượng khác thay thế.
- Nhãn phải nói đúng đại lượng được đo (ví dụ "PING (EDGE WARP)" kèm chú thích "RTT tới edge —
  KHÔNG phải ping server game").

**Lý do:** tính năng chọn node + đo ping từng hiển thị RTT thật cộng hằng số theo nhãn cộng
`Random`, và ô LOSS luôn hiện 0% theo thiết kế. Nó làm mù công cụ chẩn đoán trong nhiều tháng,
trong khi 49% kết nối tới server game thực tế thất bại.

### III. Cấu hình mạng đã kiểm chứng là bất biến

Các giá trị sau đã được đo và **không được đổi lại** nếu không có bằng chứng thực nghiệm mới,
kèm ghi chú ngày tháng:

| Cấu hình | Giá trị bắt buộc | Ghi chú |
|---|---|---|
| `tun.stack` | `mixed` | Không dùng `gvisor` — gãy TLS/cURL trong addon game |
| `tun.find-process-mode` | `always` | Đã thử `strict` 2 lần, revert cả 2. **Không thử lần thứ ba** |
| WireGuard block | `keepalive: 25`, `udp: true` | Chống rớt UDP khi NAT timeout |
| `inet4-route-exclude-address` | raw IP only | mihomo crash fatal khi parse hostname |
| `tun.inet6-address` | `[]` (rỗng, bắt buộc) | Để mặc định sẽ hút toàn bộ IPv6 vào hố đen |
| `tun.interface-name` | không set cứng | Set cứng làm handshake fail 100% (mihomo#1728) |
| DNS | `enhanced-mode: redir-host`, `ipv6: false`, `dns-hijack: any:53` | `fake-ip` đã bỏ hẳn có chủ đích |

- **Không dùng `DOMAIN-SUFFIX` để vá lỗi process-detection** — phá vỡ split tunneling, đẩy traffic
  không phải game vào tunnel WARP+ giới hạn băng thông.
- **Không dùng proxy-group `fallback`** cho ca `103.197.172.0/24`: health-check của nó đi bằng
  HTTP, mà HTTP qua tunnel vẫn OK trong lúc episode xảy ra, nên group không bao giờ chuyển.
- **Không chọn node theo vùng địa lý.** Mọi "node" là IP anycast; PoP do BGP của ISP quyết định.
  Nhãn "Taiwan/Hong Kong" là chữ trang trí, không có cơ chế phía sau.

### IV. Ranh giới với dự án khác

`DXVK_Project` là project C++/CMake độc lập do agent khác phụ trách. Từ repo này:

- **Chỉ** viết code C#/XAML để orchestrate các file `.exe` có sẵn.
- **Không sửa** bất kỳ `.cpp`/`.h` hay `installer/AOWLauncher/Program.cs` bên đó.
- Nguồn launcher chính thức là bản publish tay trong `DXVK_Project\installer\`, **không** phải
  output `dotnet build`/`dotnet publish` (thiếu payload, chạy sẽ crash). Kiểm tra size ~16MB.

### V. Mọi thay đổi hệ thống phải khôi phục được

- **Không đổi MTU qua `netsh`** — từng gây mất mạng tạm thời và kẹt MTU vĩnh viễn khi app bị kill
  giữa chừng. Chỉ chỉnh `TcpAckFrequency`/`TcpNoDelay` qua registry.
- Backup phải **persist ra đĩa** (`network_backup.json`), không chỉ trong RAM, và phải có đường
  khôi phục lúc khởi động (`RecoverPendingChangesAsync`).
- **Không xoá `Data\warp_account.json`** để "làm mới" tài khoản — thao tác đó làm mất license
  WARP+ vĩnh viễn.

### VI. Một đường build, một đường release

- Build **chỉ** bằng `dotnet build WarpGameAccelerator/WarpGameAccelerator.csproj -c Debug -r win-x64`.
  Không dùng Visual Studio (output khác path, gây "sửa rồi mà vẫn chạy bản cũ").
- Phải đóng hẳn app đang chạy trước khi build lại (MSB3027/MSB3021).
- Release bằng tag `vX.Y.Z` → GitHub Actions build + publish. Không build release thủ công.

## Additional Constraints

- **Stack:** C# .NET 8, WinUI 3 (`net8.0-windows10.0.19041.0`), DI qua
  `Microsoft.Extensions.DependencyInjection` (Singleton), MVVM qua `CommunityToolkit.Mvvm`.
- **Đa ngôn ngữ:** VIE/ENG qua `LocalizationService`, hot-swap không cần restart. Chuỗi mới phải
  có đủ cả hai ngôn ngữ.
- **Navigation:** footer nav index cố định `[0]=multiclient, [1]=warpaccount, [2]=settings,
  [3]=exit` — thêm item phải cập nhật `UpdateNavItemLabels()`.
- **Lưu trữ:** mọi state trong `%LocalAppData%\WarpGameAccelerator\`, không lưu cạnh `.exe`
  (mất khi update).
- **Chẩn đoán trước khi sửa:** đếm số tiến trình `WarpGameAccelerator` trước tiên (app không chặn
  đa instance; instance thứ hai sẽ kill mihomo của instance thứ nhất).

## Development Workflow

Ba loại công việc, ba quy trình khác nhau — **không** dùng chung một cỗ máy:

| Loại | Quy trình | Artifact |
|---|---|---|
| **Feature mới** | spec-kit đầy đủ: `specify → clarify → plan → tasks → implement` | `specs/NNN-*/` |
| **Hotfix 1–2 file** | commit thẳng, không ceremony | commit message |
| **Experiment / điều tra** | ghi giả thuyết + phép đo dự kiến **trước** khi chạy; kết quả vào `docs/` | `docs/*-findings.md`, `experiments/` |

Cổng chất lượng:

- Spec phải có **tiêu chí nghiệm thu đo được** (con số + điều kiện + thời lượng). Từ "nhanh hơn",
  "ổn định hơn" không kèm số là spec chưa đạt.
- Trước khi implement: `/speckit-analyze` để đối chiếu spec ↔ plan ↔ tasks.
- Trước khi tag release: đối chiếu lại Principle I và II; mọi thay đổi cấu hình mạng phải kèm
  bằng chứng đo trong commit message.

## Governance

- Hiến pháp này **thắng** mọi quy ước khác trong repo khi có xung đột. `CLAUDE.md` giữ vai trò
  "cách build/chạy + cây bẫy cụ thể" và không được mâu thuẫn với hiến pháp.
- Sửa đổi hiến pháp phải nêu: **bằng chứng mới**, phạm vi ảnh hưởng, và ngày hiệu lực. Ghi lại
  trong commit message.
- Mọi review phải kiểm tra tuân thủ. Vi phạm Principle I hoặc II là lỗi chặn, không phải nitpick.
- Tính đơn giản được ưu tiên: không thêm lớp trừu tượng cho yêu cầu giả định.

**Version**: 1.0.0 | **Ratified**: 2026-10-29 | **Last Amended**: 2026-10-29
