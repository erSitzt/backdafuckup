#!/bin/bash
# Rebuilds the file-level restore test images in this folder (*.img.zst + *.manifest).
# Needs Docker. The container runs --privileged because it uses loop devices, FUSE (ntfs-3g), device-mapper (LVM)
# and kernel mounts; it only touches its own image files and loop devices.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
docker build -q -t backdafuckup/fs-fixtures:1 "$here" >/dev/null
docker run --rm --privileged -e OWNER="$(id -u):$(id -g)" -v "$here:/out" -v "$here/build.sh:/build.sh:ro" \
    backdafuckup/fs-fixtures:1 bash /build.sh
