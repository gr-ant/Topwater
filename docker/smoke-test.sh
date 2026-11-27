#!/bin/sh
# Smoke test for the topwater image: docker/smoke-test.sh IMAGE
set -eu
img=$1
run() { timeout 120 docker run --rm --log-driver none "$@"; }

echo "== run"
out=$(run "$img" run 'echo ok; git --version')
printf '%s\n' "$out"
[ "$(printf '%s\n' "$out" | head -n1)" = ok ]
printf '%s\n' "$out" | grep -q '^git version '

echo "== non-root"
user=$(docker image inspect --format '{{.Config.User}}' "$img")
echo "user: $user"
case "$user" in ''|0|root|0:*|root:*) echo "image runs as root" >&2; exit 1 ;; esac

echo "== mcp over stdio"
out=$(printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bash","arguments":{"command":"echo from-mcp; whoami"}}}' \
  | run -i "$img" mcp)
printf '%s\n' "$out"
printf '%s\n' "$out" | grep '"id":1,' | grep -q '"serverInfo":{"name":"topwater"'
printf '%s\n' "$out" | grep '"id":2,' | grep -q '"name":"bash"'
printf '%s\n' "$out" | grep '"id":3,' | grep -q 'from-mcp'
if printf '%s\n' "$out" | grep '"id":3,' | grep -q '"isError":true'; then echo "bash call failed" >&2; exit 1; fi

echo "== /data belongs to the image's user (a fresh volume there is writable)"
run "$img" run --state machine.topwater true

echo "== --state on a mounted folder persists between runs"
dir=$(mktemp -d)
chmod 777 "$dir"   # the image's user (uid 1654) writes here
run -v "$dir:/data" "$img" run --state /data/m.topwater 'echo kept > ~/note'
[ -s "$dir/m.topwater" ]
out=$(run -v "$dir:/data" "$img" run --state /data/m.topwater 'cat ~/note')
rm -rf "$dir"
printf '%s\n' "$out"
[ "$out" = kept ]

echo "== a --state folder the image's user cannot write fails at start"
dir=$(mktemp -d)   # mode 700, owned by the caller, not by uid 1654
if run -v "$dir:/data" "$img" run --state /data/m.topwater 'echo should-not-run'; then rm -rf "$dir"; echo "exit 0 with an unwritable --state" >&2; exit 1; fi
rm -rf "$dir"

echo "== size"
docker image inspect --format '{{.Size}}' "$img" | awk '{ printf "%.1f MB\n", $1 / 1000000 }'
echo "all passed"
