# Project: WarpGameAccelerator Direct Mode Cloudflare WARP Fix

## Architecture
- **Target Application**: WarpGameAccelerator (WinUI 3 .NET app)
- **Engine**: Mihomo (`mihomo.exe`) embedded proxy core & WireGuard driver/TUN
- **Objective**: Direct Mode Cloudflare WARP connection bypasses DPI and connects natively without needing an external proxy process (`warp_proxy.exe`), while ensuring game TCP/UDP traffic routes cleanly and HTTP/SOCKS5 traffic can be verified via curl.

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | M1: Exploration & Root Cause Analysis | Analyze v1.6.9 source/history/config, wgcf source, working wgcf configs vs mihomo WireGuard, identify DPI blocking cause | none | DONE |
| 2 | M2: Implementation of Definitive Fix | Update mihomo config / reserved bytes / binary / code in WarpGameAccelerator to bypass DPI natively without standalone proxy if possible | M1 | DONE |
| 3 | M3: Verification & Audit | Code Review, empirical curl & ping execution over tunnel, Forensic Integrity Audit | M2 | DONE |

## Interface Contracts
### Mihomo ↔ Cloudflare WARP WireGuard Interface
- WireGuard protocol params:
  - `name`: WARP-Direct
  - `type`: wireguard
  - `server`: endpoint IP (e.g., `162.159.192.1` or `162.159.193.1`)
  - `port`: `2408` (or `500` / `1708` / `4500`)
  - `ip`: `172.16.0.2`
  - `public-key`: `bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=`
  - `private-key`: client private key
  - `reserved`: `[x, y, z]` (3 bytes decoded from `client_id` via `Convert.FromBase64String(client_id)`)
  - `mtu`: `1280`
  - `udp`: `true`
  - `remote-dns-resolve`: `true`
- Mihomo TUN stack:
  - `stack`: `mixed`
  - `mtu`: `1280`
  - `auto-route`: `true`
  - `inet4-route-exclude-address`: `[endpoint_ip/32]`
  - `dns.enhanced-mode`: `redir-host`

## Code Layout
- `Services/MihomoService.cs` - Core engine initialization, configuration generation, TUN & proxy routing setup.
- `Services/WarpAccountService.cs` - WARP account registration, client ID & reserved bytes extraction (`Convert.FromBase64String(clientId)`).
- `Publish/` - Local build output directory (`c:\Users\annh\Documents\AOW_Booster\Publish` or project Publish folder).
