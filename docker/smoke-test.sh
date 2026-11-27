#!/bin/sh
# Smoke test for the topwater image: docker/smoke-test.sh IMAGE
set -eu
img=$1
run() { timeout 120 docker run --rm --log-driver none "$@"; }

echo "== run"
out=$(run "$img" run 'echo ok; git --version')
echo "$out"
[ "$(echo "$out" | head -n1)" = ok ]
echo "$out" | grep -q '^git version '

echo "== non-root"
user=$(docker image inspect --format '{{.Config.User}}' "$img")
echo "user: $user"
case "$user" in ''|0|root|0:*|root:*) echo "image runs as root" >&2; exit 1 ;; esac

echo "== mcp over stdio"
# one request at a time, reading each answer before the next: the server stops reading at end of input
fifo=$(mktemp -d)
mkfifo "$fifo/in" "$fifo/out"
timeout 120 docker run -i --rm --log-driver none "$img" mcp < "$fifo/in" > "$fifo/out" &
exec 3> "$fifo/in" 4< "$fifo/out"
ask() {   # ask ID JSON: send JSON, print the answer to request ID
  printf '%s\n' "$2" >&3
  while IFS= read -r line <&4; do
    case "$line" in *"\"id\":$1,"*|*"\"id\":$1}"*) printf '%s\n' "$line"; return 0 ;; esac
  done
  echo "no answer to request $1" >&2; return 1
}
ask 1 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' | tee "$fifo/r1"
echo '{"jsonrpc":"2.0","method":"notifications/initialized"}' >&3
ask 2 '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | tee "$fifo/r2"
ask 3 '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bash","arguments":{"command":"echo from-mcp; whoami"}}}' | tee "$fifo/r3"
exec 3>&- 4<&-
wait
grep -q '"serverInfo":{"name":"topwater"' "$fifo/r1"
grep -q '"name":"bash"' "$fifo/r2"
grep -q 'from-mcp' "$fifo/r3"
grep -q '"isError":true' "$fifo/r3" && { echo "bash call failed" >&2; exit 1; }
rm -rf "$fifo"

echo "== /data belongs to the image's user (a fresh volume there is writable)"
run "$img" run --state machine.topwater true

echo "== --state on a mounted folder persists between runs"
dir=$(mktemp -d)
chmod 777 "$dir"   # the image's user (uid 1654) writes here
run -v "$dir:/data" "$img" run --state /data/m.topwater 'echo kept > ~/note'
[ -s "$dir/m.topwater" ]
out=$(run -v "$dir:/data" "$img" run --state /data/m.topwater 'cat ~/note')
rm -rf "$dir"
echo "$out"
[ "$out" = kept ]

echo "== size"
docker image inspect --format '{{.Size}}' "$img" | awk '{ printf "%.1f MB\n", $1 / 1000000 }'
echo "all passed"
