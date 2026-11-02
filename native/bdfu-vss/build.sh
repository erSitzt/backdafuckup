#!/usr/bin/env bash
# Builds bdfu-vss.dll (Windows x64) with mingw-w64 in Docker; no Windows SDK or VSS SDK needed.
# Usage: native/bdfu-vss/build.sh [output directory]   (default: artifacts/native)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/../../artifacts/native}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
docker build -q -t backdafuckup/mingw-vss:1 "$here" >/dev/null
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e TMPDIR=/tmp -v "$here:/src:ro" -v "$out:/out" backdafuckup/mingw-vss:1 bash -ec '
  x86_64-w64-mingw32-g++ -std=c++17 -O2 -Wall -Wextra -Werror -shared -DUNICODE -D_UNICODE \
    -o /out/bdfu-vss.dll /src/bdfu-vss.cpp /src/bdfu-vss.def \
    -static -static-libgcc -static-libstdc++ -lvssapi -lole32 -loleaut32 -luuid -ladvapi32
  x86_64-w64-mingw32-strip /out/bdfu-vss.dll
'
echo "Built: $out/bdfu-vss.dll"
