# Tools for the file-level restore test images (scripts/make-fs-images.sh runs this image with --privileged,
# because NTFS (ntfs-3g/FUSE), LVM (device-mapper) and the kernel-written XFS/ext4 variants need loop devices).
FROM debian:trixie
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      e2fsprogs xfsprogs ntfs-3g lvm2 fdisk util-linux attr zstd perl coreutils findutils \
 && rm -rf /var/lib/apt/lists/*
