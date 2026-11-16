#!/usr/bin/env bash
# Builds the tape agent for Linux as one self-contained file, with its systemd unit:
#   artifacts/tape-agent/backdafuckup-tape-agent
#   artifacts/tape-agent/backdafuckup-tape-agent.service
#   artifacts/tape-agent/version.txt
#
# Usage: scripts/build-tape-agent.sh [output directory]   (default: artifacts/tape-agent)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/tape-agent}"
version="${TAPE_AGENT_VERSION:-$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== tape agent (linux-x64, self-contained, one file)"
"$dotnet" publish "$root/src/Backdafuckup.TapeAgent" -c Release -r linux-x64 -p:Version="$version" \
  -o "$work/publish" --artifacts-path "$work/artifacts" --nologo -v quiet

rm -rf "$out"
mkdir -p "$out"
install -m 755 "$work/publish/backdafuckup-tape-agent" "$out/"
cp "$root/deploy/linux/backdafuckup-tape-agent.service" "$out/"
echo "$version" > "$out/version.txt"
echo "Tape agent $version:"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
