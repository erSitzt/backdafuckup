#!/usr/bin/env bash
# Builds the backup proxy agent for Linux as one self-contained file, with its systemd unit:
#   artifacts/proxy/backdafuckup-proxy
#   artifacts/proxy/backdafuckup-proxy.service
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

echo "== proxy agent (linux-x64, self-contained, one file)"
"$dotnet" publish "$root/src/Backdafuckup.ProxyAgent" -c Release -r linux-x64 -p:Version="$version" \
  -o "$work/publish" --artifacts-path "$work/artifacts" --nologo -v quiet

rm -rf "$out"
mkdir -p "$out"
install -m 755 "$work/publish/backdafuckup-proxy" "$out/"
cp "$root/deploy/linux/backdafuckup-proxy.service" "$out/"
echo "$version" > "$out/version.txt"
echo "Proxy agent $version:"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
