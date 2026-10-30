#!/usr/bin/env bash
# Experiment A1: does the local UDP source port determine Cloudflare colo / reachability?
# Harness: wireproxy pins the WireGuard ListenPort; SOCKS5 exposes the tunnel.
# Signal: colo from 1.1.1.1/cdn-cgi/trace + TCP reachability to the game server.
set -u
cd "$(dirname "$0")"
CURL=/c/Windows/System32/curl.exe
SOCKS=127.0.0.1:25344
GAME=103.197.172.23:4000
OUT=results.csv

python - <<'PY'
import json, os
p = os.path.join(os.environ['LOCALAPPDATA'], 'WarpGameAccelerator', 'Data', 'warp_account.json')
d = json.load(open(p, encoding='utf-8-sig'))
open('base.json', 'w').write(json.dumps({k: d[k] for k in ('IPv4', 'PrivateKey', 'PeerPublicKey')}))
PY

write_conf() {
python - "$1" <<'PY'
import json, sys
b = json.load(open('base.json'))
open('run.conf', 'w').write(f"""[Interface]
Address = {b['IPv4']}/32
PrivateKey = {b['PrivateKey']}
MTU = 1280
ListenPort = {sys.argv[1]}

[Peer]
PublicKey = {b['PeerPublicKey']}
Endpoint = 162.159.192.1:2408
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25

[Socks5]
BindAddress = 127.0.0.1:25344
""")
PY
}

echo "port,local_port,colo,egress_ip,sliver,game_rc,trace_rc" > "$OUT"

for PORT in $(seq 20000 20031); do
  write_conf "$PORT"
  taskkill //F //IM wireproxy.exe >/dev/null 2>&1
  ./bin/wireproxy.exe -c run.conf > /dev/null 2>&1 &
  sleep 4

  L4=$(netstat -ano -p UDP 2>/dev/null | awk -v p=":$PORT" '$2 ~ p {print $2; exit}')
  TRACE=$("$CURL" -s --max-time 12 --socks5-hostname "$SOCKS" https://1.1.1.1/cdn-cgi/trace 2>/dev/null)
  TRC=$?
  COLO=$(printf '%s' "$TRACE" | awk -F= '/^colo=/{print $2}')
  IP=$(printf '%s' "$TRACE" | awk -F= '/^ip=/{print $2}')
  SLIVER=$(printf '%s' "$TRACE" | awk -F= '/^sliver=/{print $2}')

  "$CURL" -s -o /dev/null --max-time 8 --socks5-hostname "$SOCKS" "http://$GAME/" 2>/dev/null
  GRC=$?

  echo "$PORT,$L4,${COLO:--},${IP:--},${SLIVER:--},$GRC,$TRC" >> "$OUT"
  echo "[$PORT] colo=${COLO:--} ip=${IP:--} game_rc=$GRC trace_rc=$TRC"
  sleep 1
done

taskkill //F //IM wireproxy.exe >/dev/null 2>&1
rm -f base.json run.conf
echo "=== DONE ==="
