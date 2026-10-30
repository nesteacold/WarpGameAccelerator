#!/usr/bin/env bash
# Experiment A3: compare our own WARP tunnel against Cloudflare's official warp-svc
# in the SAME fault window. Closes the "only the official client is prioritised" hypothesis.
# Safe: only flips warp-cli into proxy mode when a fault window is actually open, and restores it.
set -u
cd "$(dirname "$0")"
C=/c/Windows/System32/curl.exe
WARPC=/c/Program\ Files/Cloudflare/Cloudflare\ WARP/warp-cli
GAME=103.197.172.23:4000

python - <<'PY'
import json, os
d = json.load(open(os.path.join(os.environ['LOCALAPPDATA'], 'WarpGameAccelerator', 'Data', 'warp_account.json'), encoding='utf-8-sig'))
open('wg3.conf', 'w').write(f"""[Interface]
Address = {d['IPv4']}/32
PrivateKey = {d['PrivateKey']}
MTU = 1280
ListenPort = 20000

[Peer]
PublicKey = {d['PeerPublicKey']}
Endpoint = 162.159.192.1:2408
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25

[Socks5]
BindAddress = 127.0.0.1:25344
""")
PY

taskkill //F //IM wireproxy.exe >/dev/null 2>&1
sleep 1
./bin/wireproxy.exe -c wg3.conf > /dev/null 2>&1 &
sleep 7

echo "== window check (6 probes, ours) =="
FAIL=0
for i in $(seq 1 6); do
  "$C" -s -o /dev/null --max-time 6 --socks5-hostname 127.0.0.1:25344 "http://$GAME/" 2>/dev/null
  RC=$?; [ "$RC" -ne 52 ] && FAIL=$((FAIL+1))
  echo "  probe $i rc=$RC"; sleep 1
done
echo "failures: $FAIL/6"

if [ "$FAIL" -lt 2 ]; then
  echo "NO FAULT WINDOW — skipping warp-svc comparison, warp-cli untouched."
  taskkill //F //IM wireproxy.exe >/dev/null 2>&1; rm -f wg3.conf
  exit 0
fi

echo "== fault window OPEN — switching warp-cli to proxy mode =="
ORIG_MODE=$("$WARPC" --accept-tos settings 2>/dev/null | awk -F': *' '/Mode/{print $2; exit}')
echo "original mode: ${ORIG_MODE:-unknown}"
"$WARPC" --accept-tos mode proxy >/dev/null 2>&1
"$WARPC" --accept-tos proxy port 40000 >/dev/null 2>&1
"$WARPC" --accept-tos connect >/dev/null 2>&1
sleep 12
"$WARPC" --accept-tos status 2>&1 | head -2

echo "cycle,ts,ours_rc,warpsvc_rc" > a3_results.csv
for i in $(seq 1 12); do
  "$C" -s -o /dev/null --max-time 6 --socks5-hostname 127.0.0.1:25344 "http://$GAME/" 2>/dev/null; RA=$?
  "$C" -s -o /dev/null --max-time 6 --socks5-hostname 127.0.0.1:40000 "http://$GAME/" 2>/dev/null; RB=$?
  echo "$i,$(date +%H:%M:%S),$RA,$RB" >> a3_results.csv
  echo "[$i] $(date +%H:%M:%S) ours=$RA warpsvc=$RB"; sleep 2
done

echo "== restoring warp-cli =="
"$WARPC" --accept-tos disconnect >/dev/null 2>&1
[ -n "${ORIG_MODE:-}" ] && "$WARPC" --accept-tos mode "${ORIG_MODE}" >/dev/null 2>&1
"$WARPC" --accept-tos status 2>&1 | head -2
taskkill //F //IM wireproxy.exe >/dev/null 2>&1; rm -f wg3.conf
echo "=== DONE ==="
