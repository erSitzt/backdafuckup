#!/usr/bin/env bash
# Builds the mover's container image, which the backup server pushes to the registry set in the console (Kubernetes
# clusters → Mover image) and starts next to each volume. No Docker needed: the .NET SDK writes the image as an archive.
#   artifacts/mover/backdafuckup-mover.tar  image archive (manifest.json, the config, gzip-compressed layers)
#   artifacts/mover/version.txt
#
# Usage: scripts/build-mover.sh [output directory]   (default: artifacts/mover)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/mover}"
# The product version (Directory.Build.props); MOVER_VERSION overrides it.
version="${MOVER_VERSION:-$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props")}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "== mover image (linux-x64, trimmed, base runtime-deps chiseled)"
"$dotnet" publish "$root/src/Backdafuckup.Mover" -c Release -r linux-x64 -t:PublishContainer -p:Version="$version" \
  -p:ContainerImageTag="$version" -p:ContainerArchiveOutputPath="$work/backdafuckup-mover.tar" -o "$work/publish" --nologo -v quiet

rm -rf "$out"
mkdir -p "$out"
cp "$work/backdafuckup-mover.tar" "$out/"
echo "$version" > "$out/version.txt"
echo "Mover $version:"
find "$out" -type f -printf '  %P  %s bytes\n' | sort
