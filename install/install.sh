#!/bin/sh
# Installs the topwater command on Linux or macOS: https://github.com/gr-ant/Topwater
#
#   curl -fsSL https://gr-ant.github.io/Topwater/install.sh | sh
#
# Settings (environment variables):
#   TOPWATER_VERSION       a release tag such as v0.1.0-preview.3 (default: the newest release, previews included)
#   TOPWATER_INSTALL_DIR   where the binary goes (default: ~/.local/bin)
#   TOPWATER_DOWNLOAD_URL  where the release files are (default: the GitHub release for the version)
#
# Uninstall: rm ~/.local/bin/topwater
set -eu

repo=gr-ant/Topwater
dir=${TOPWATER_INSTALL_DIR:-$HOME/.local/bin}

fail() { echo "topwater install: $*" >&2; exit 1; }

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) fail "no build for $(uname -s); on Windows, run install.ps1 in PowerShell" ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) fail "no build for a $(uname -m) processor" ;;
esac

base=${TOPWATER_DOWNLOAD_URL:-}
tag=${TOPWATER_VERSION:-}
if [ -z "$base" ]; then
  if [ -z "$tag" ]; then
    tag=$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=1" |
      sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1)
    [ -n "$tag" ] || fail "could not find a release of $repo"
  fi
  base=https://github.com/$repo/releases/download/$tag
fi

name=topwater-$os-$arch.tar.gz
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL -o "$tmp/$name" "$base/$name" || fail "could not download $base/$name"
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS" || fail "could not download $base/SHA256SUMS"

expected=$(awk -v n="$name" '$2 == n || $2 == "*" n { print $1 }' "$tmp/SHA256SUMS")
if command -v sha256sum >/dev/null 2>&1; then
  actual=$(sha256sum "$tmp/$name" | cut -d ' ' -f 1)
else
  actual=$(shasum -a 256 "$tmp/$name" | cut -d ' ' -f 1)
fi
[ -n "$expected" ] && [ "$expected" = "$actual" ] || fail "checksum of $name does not match SHA256SUMS"

tar -xzf "$tmp/$name" -C "$tmp"
mkdir -p "$dir"
cp "$tmp/topwater" "$dir/topwater.new"
chmod 755 "$dir/topwater.new"
mv -f "$dir/topwater.new" "$dir/topwater"

echo "Installed topwater${tag:+ $tag} to $dir/topwater"
case ":$PATH:" in
  *":$dir:"*) echo "Run: topwater --help" ;;
  *) echo "$dir is not on your PATH. Add it, for example: echo 'export PATH=\"$dir:\$PATH\"' >> ~/.bashrc" ;;
esac
