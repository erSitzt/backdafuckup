#!/usr/bin/env bash
# Builds bdfu-tape-recover, which reads backdafuckup tapes without the server, as one self-contained file per platform:
#   artifacts/tape-recover/linux-x64/bdfu-tape-recover
#   artifacts/tape-recover/win-x64/bdfu-tape-recover.exe
#
# Usage: scripts/build-tape-recover.sh [output directory]   (default: artifacts/tape-recover)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/tape-recover}"
version="${TAPE_RECOVER_VERSION:-$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

rm -rf "$out"
for rid in linux-x64 win-x64; do
  echo "== bdfu-tape-recover ($rid, self-contained, one file)"
  "$dotnet" publish "$root/src/Backdafuckup.TapeRecover" -c Release -r "$rid" -p:Version="$version" \
    -o "$work/$rid" --artifacts-path "$work/artifacts-$rid" --nologo -v quiet
  mkdir -p "$out/$rid"
  cp "$work/$rid"/bdfu-tape-recover* "$out/$rid/"
done
echo "$version" > "$out/version.txt"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
