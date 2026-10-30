#!/usr/bin/env bash
# Experiment A1b: two tunnels pinned to different source ports, probed interleaved.
# If failures hit both -> time-based (network weather). If only one -> port/egress-based.
set -u
cd "$(dirname "$0")"
CURL=/c/Windows/System32/curl.exe
GAME=103.197.172.23:4000
PA=20000; SA=25344
PB=20017; SB=25345

python - <<'PY'
import json, os
d = json.load(open(os.path.join(os.environ['LOCALAPPDATA'], 'WarpGameAccelerator', 'Data', 'warp_account.json'), encoding='utf-8-sig'))
for name, port, socks in (('a', 20000, 25344), ('b', 20017, 25345)):
    open(f'{name}.conf', 'w').write(f"""[Interface]
Address = 172.16.0.2/32
PrivateKey = {d['PrivateKey']}
MTU = 1280
ListenPort = {port}

[Peer]
PublicKey = {d['PeerPublicKey']}
Endpoint = 162.159.192.1:2408
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25

[Socks5]
BindAddress = 127.0.0.1:{socks}
""")
PY

taskkill //F //IM wireproxy.exe >/dev/null 2>&1
sleep 2
./bin/wireproxy.exe -c a.conf > /dev/null 2>&1 &
./bin/wireproxy.exe -c b.conf > /dev/null 2>&1 &
sleep 7

EGRA=$(curl -s --max-time 10 --socks5-hostname 127.0.0.1:$SA https://1.1.1.1/cdn-cgi/trace | awk -F= '/^ip=|^colo=/{printf "%s ", $2}')
EGRB=$(curl -s --max-time 10 --socks5-hostname 127.0.0.1:$SB https://1.1.1.1/cdn-cgi/trace | awk -F= '/^ip=|^colo=/{printf "%s ", $2}')
echo "A($PA) = $EGRA | B($PB) = $EGRB"
echo "cycle,ts,a_rc,b_rc" > ab_results.csv

for i in $(seq 1 20); do
  "$CURL" -s -o /dev/null --max-time 6 --socks5-hostname 127.0.0.1:$SA "http://$GAME/" 2>/dev/null; RA=$?
  "$CURL" -s -o /dev/null --max-time 6 --socks5-hostname 127.0.0.1:$SB "http://$GAME/" 2>/dev/null; RB=$?
  TS=$(date +%H:%M:%S)
  echo "$i,$TS,$RA,$RB" >> ab_results.csv
  echo "[$i] $TS  A=$RA  B=$RB"
  sleep 2
done

taskkill //F //IM wireproxy.exe >/dev/null 2>&1
rm -f a.conf b.conf
echo "=== DONE ==="
