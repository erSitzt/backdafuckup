#!/usr/bin/env bash
# Builds the backup proxy agent as one self-contained file per platform, with the Linux systemd unit:
#   artifacts/proxy/backdafuckup-proxy          (linux-x64)
#   artifacts/proxy/backdafuckup-proxy.service
#   artifacts/proxy/backdafuckup-proxy.exe      (win-x64, physical Windows servers with SAN access)
#   artifacts/proxy/version.txt
#
# Usage: scripts/build-proxy.sh [output directory]   (default: artifacts/proxy)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/proxy}"
version="${PROXY_VERSION:-$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in linux-x64 win-x64; do
  echo "== proxy agent ($rid, self-contained, one file)"
  "$dotnet" publish "$root/src/Backdafuckup.ProxyAgent" -c Release -r "$rid" -p:Version="$version" \
    -o "$work/publish-$rid" --artifacts-path "$work/artifacts-$rid" --nologo -v quiet
done

rm -rf "$out"
mkdir -p "$out"
install -m 755 "$work/publish-linux-x64/backdafuckup-proxy" "$out/"
cp "$root/deploy/linux/backdafuckup-proxy.service" "$out/"
install -m 644 "$work/publish-win-x64/backdafuckup-proxy.exe" "$out/"
echo "$version" > "$out/version.txt"
echo "Proxy agent $version:"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
