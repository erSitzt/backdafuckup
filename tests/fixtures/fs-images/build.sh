#!/bin/bash
# Builds the file-level restore test images. Runs INSIDE the fixture container (see make-fs-images.sh):
# needs --privileged for loop devices, ntfs-3g (FUSE), device-mapper (LVM) and kernel mounts of ext4/XFS.
#
# Every image is a whole disk (partition table + file systems) and comes with one manifest per volume:
#   <type>\t<sha256|link target|->\t<size>\t<mode>\t<uid>\t<gid>\t<mtime>\t<path>
# The tests compare what our reader sees with these manifests.
set -euo pipefail

OUT=${OUT:-/out}
WORK=$(mktemp -d /tmp/fsimg.XXXXXX)
LVMCONF='devices { obtain_device_list_from_udev=0 } activation { udev_sync=0 udev_rules=0 }'
MOUNTS=()

# Loop devices live in the kernel of the Docker VM and outlive the container. Only devices backed by files of
# this script (/tmp/fsimg.*) are detached; others (e.g. Docker Desktop's own) are never touched.
detach_ours() { # prefix
    losetup -l -n -O NAME,BACK-FILE | awk -v p="$1" 'index($2, p) == 1 { print $1 }' | while read -r d; do losetup -d "$d"; done
}

cleanup() {
    set +e
    for ((i=${#MOUNTS[@]}-1; i>=0; i--)); do umount "${MOUNTS[$i]}" 2>/dev/null; done
    vgchange -an --config "$LVMCONF" vgflr >/dev/null 2>&1
    detach_ours "$WORK/"
    rm -rf "$WORK"
}
trap cleanup EXIT

loop() { # file [offset size] -> prints the loop device
    if [ $# -eq 3 ]; then losetup --find --show --offset "$2" --sizelimit "$3" "$1"; else losetup --find --show "$1"; fi
}

mountfs() { # device dir [mount args...]
    local dev=$1 dir=$2; shift 2
    mkdir -p "$dir"
    mount "$@" "$dev" "$dir"
    MOUNTS+=("$dir")
}

unmount() {
    umount "$1"
    # ntfs-3g (FUSE) may still be writing after umount returns.
    while grep -qsx ntfs-3g /proc/[0-9]*/comm; do sleep 0.2; done
    local i; for i in "${!MOUNTS[@]}"; do [ "${MOUNTS[$i]}" = "$1" ] && unset 'MOUNTS[$i]'; done; MOUNTS=("${MOUNTS[@]}")
}

part() { # image partition-number -> "start_bytes size_bytes"
    sfdisk -d "$1" | awk -v n="$1$2" '$1 == n { gsub(/[,=]/, " "); for (i = 1; i <= NF; i++) { if ($i == "start") s = $(i + 1); if ($i == "size") z = $(i + 1) } print s * 512, z * 512 }'
}

# Content that is different in every 512-byte sector, so a reader that returns the wrong blocks is caught,
# but still compresses well.
pattern() { # file size name
    perl -e 'my ($n, $s) = @ARGV; for (my $o = 0; $o < $s; $o += 512) { my $l = sprintf("%s offset %012d|", $n, $o); print substr($l x (int(512 / length($l)) + 1), 0, 512); }' "$3" "$2" > "$1"
}

common_tree() { # dir: content shared by all file systems
    local d=$1
    mkdir -p "$d/folder/sub/deep" "$d/many" "$d/medium" "$d/leafdir"
    printf 'Hello from backdafuckup\n' > "$d/hello.txt"
    : > "$d/empty.txt"
    printf 'umlaut\n' > "$d/Ümlaut äöü ß.txt"
    printf 'deep\n' > "$d/folder/sub/deep/nested.txt"
    printf 'long\n' > "$d/$(printf 'L%.0s' $(seq 1 200)).txt"
    pattern "$d/pattern-3m.bin" 3145728 pattern-3m
    for i in $(seq -w 0 49); do printf '%s\n' "$i" > "$d/medium/m$i.txt"; done
    for i in $(seq -w 0 399); do printf '%s\n' "$i" > "$d/leafdir/l$i.txt"; done
    for i in $(seq -w 0 2999); do printf '%s\n' "$i" > "$d/many/f$i.txt"; done
    # sparse: data at 0 and at 16 MiB, 32 MiB long
    truncate -s 32M "$d/sparse.bin"
    pattern "$WORK/s1" 65536 sparse-a; dd if="$WORK/s1" of="$d/sparse.bin" conv=notrunc bs=64k seek=0 status=none
    pattern "$WORK/s2" 65536 sparse-b; dd if="$WORK/s2" of="$d/sparse.bin" conv=notrunc bs=64k seek=256 status=none
}

linux_extras() { # dir: things only Linux file systems have
    local d=$1
    ln -s hello.txt "$d/link-to-hello"
    ln -s folder/sub "$d/link-to-dir"
    ln "$d/hello.txt" "$d/hello-hardlink.txt"
    printf 'secret\n' > "$d/secret.txt"; chmod 0640 "$d/secret.txt"
    printf 'owned\n' > "$d/owned.txt"; chown 1000:1000 "$d/owned.txt"
    touch -d '2001-02-03 04:05:06 UTC' "$d/hello.txt"
}

manifest() { # root output
    (cd "$1" && find . -mindepth 1 -not -path './lost+found' -not -path './lost+found/*' -print0 | sort -z | while IFS= read -r -d '' p; do
        local rel=${p#./} st
        st=$(stat -c '%a	%u	%g	%Y' "$p")
        if [ -L "$p" ]; then printf 'link\t%s\t0\t%s\t%s\n' "$(readlink "$p")" "$st" "$rel"
        elif [ -d "$p" ]; then printf 'dir\t-\t0\t%s\t%s\n' "$st" "$rel"
        else printf 'file\t%s\t%s\t%s\t%s\n' "$(sha256sum < "$p" | cut -d' ' -f1)" "$(stat -c %s "$p")" "$st" "$rel"; fi
    done) > "$2"
}

finish() { # image name
    sync
    zstd -19 --long=27 -T0 -q -f "$1" -o "$OUT/$2.img.zst"
    rm -f "$1"
    echo "built $2 ($(du -h "$OUT/$2.img.zst" | cut -f1) compressed)"
}

# Junk in free blocks: a reader that returns old blocks for preallocated (unwritten) extents is caught.
prealloc() { # dir
    pattern "$1/junk.bin" 2097152 junk; sync; rm "$1/junk.bin"; sync
    fallocate -l 1M "$1/prealloc.bin"
}

# Leftovers of an earlier, aborted run of this script.
vgchange -an --config "$LVMCONF" vgflr >/dev/null 2>&1 || true
detach_ours /tmp/fsimg.

# Loop devices created after the container started have no device node here.
for i in $(seq 0 63); do [ -e "/dev/loop$i" ] || mknod -m 0660 "/dev/loop$i" b 7 "$i"; done

mkdir -p "$OUT"
{
    echo "debian $(cat /etc/debian_version)"
    mke2fs -V 2>&1 | head -1
    mkfs.xfs -V
    ntfs-3g --version 2>&1 | head -1
    lvm version | head -1
    uname -r
} > "$OUT/versions.txt"

# ---------------------------------------------------------------- NTFS on GPT
img=$WORK/ntfs-gpt.img
truncate -s 64M "$img"
printf 'label: gpt\nstart=2048, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, name="Basic data partition"\n' | sfdisk -q "$img"
read -r off size < <(part "$img" 1)
dev=$(loop "$img" "$off" "$size")
mkntfs -Q -F -q -p $((off / 512)) -H 255 -S 63 -L FLRNTFS "$dev"
mkdir -p "$WORK/m"; ntfs-3g "$dev" "$WORK/m" -o streams_interface=windows,compression; MOUNTS+=("$WORK/m")
common_tree "$WORK/m"
printf 'stream data\n' > "$WORK/m/hello.txt:stream1"
mkdir "$WORK/m/compressed"; setfattr -h -v 0x00000800 -n system.ntfs_attrib_be "$WORK/m/compressed"
pattern "$WORK/m/compressed/packed.bin" 1048576 packed
printf 'hidden\n' > "$WORK/m/hidden.txt"; setfattr -h -v 0x00000002 -n system.ntfs_attrib_be "$WORK/m/hidden.txt"
manifest "$WORK/m" "$OUT/ntfs-gpt.p1.manifest"
printf 'stream\t%s\t12\t-\t-\t-\t-\thello.txt:stream1\n' "$(printf 'stream data\n' | sha256sum | cut -d' ' -f1)" >> "$OUT/ntfs-gpt.p1.manifest"
unmount "$WORK/m"
finish "$img" ntfs-gpt

# ---------------------------------------------------------------- NTFS on MBR with a logical partition
img=$WORK/ntfs-mbr.img
truncate -s 96M "$img"
printf 'label: dos\nstart=2048, size=81920, type=7\nstart=83968, type=5\nstart=86016, type=7\n' | sfdisk -q "$img"
read -r off size < <(part "$img" 1)
dev=$(loop "$img" "$off" "$size"); mkntfs -Q -F -q -p $((off / 512)) -H 255 -S 63 -L PRIMARY "$dev"
ntfs-3g "$dev" "$WORK/m"; MOUNTS+=("$WORK/m")
printf 'primary\n' > "$WORK/m/hello.txt"; mkdir -p "$WORK/m/folder"; printf 'x\n' > "$WORK/m/folder/x.txt"
manifest "$WORK/m" "$OUT/ntfs-mbr.p1.manifest"; unmount "$WORK/m"
read -r off size < <(part "$img" 5)
dev=$(loop "$img" "$off" "$size"); mkntfs -Q -F -q -p $((off / 512)) -H 255 -S 63 -L LOGICAL "$dev"
ntfs-3g "$dev" "$WORK/m"; MOUNTS+=("$WORK/m")
printf 'logical\n' > "$WORK/m/hello.txt"
manifest "$WORK/m" "$OUT/ntfs-mbr.p5.manifest"; unmount "$WORK/m"
finish "$img" ntfs-mbr

# ---------------------------------------------------------------- ext4 on GPT (current mke2fs defaults, kernel-written)
img=$WORK/ext4-gpt.img
truncate -s 96M "$img"
printf 'label: gpt\nstart=2048, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="root"\n' | sfdisk -q "$img"
read -r off size < <(part "$img" 1)
dev=$(loop "$img" "$off" "$size")
mke2fs -q -F -t ext4 -T default -b 4096 -L flrext4 "$dev"
mountfs "$dev" "$WORK/m"
common_tree "$WORK/m"; linux_extras "$WORK/m"; prealloc "$WORK/m"
manifest "$WORK/m" "$OUT/ext4-gpt.p1.manifest"
dumpe2fs -h "$dev" 2>/dev/null | grep -E 'features|Block size|Inode size' > "$OUT/ext4-gpt.features.txt"
unmount "$WORK/m"
finish "$img" ext4-gpt

# ---------------------------------------------------------------- XFS with nrext64 (mkfs protofile; the 5.15 kernel cannot mount it)
img=$WORK/xfs-nrext64.img
truncate -s 320M "$img"
printf 'label: gpt\nstart=2048, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="data"\n' | sfdisk -q "$img"
read -r off size < <(part "$img" 1)
tree=$WORK/tree; rm -rf "$tree"; mkdir -p "$tree"; common_tree "$tree"; linux_extras "$tree"; rm "$tree/hello-hardlink.txt"
proto() { # dir indent: protofile body (mode uid gid per entry; hard links are not supported by protofiles)
    local dir=$1 ind=$2 name p mode
    while IFS= read -r -d '' p; do
        name=${p##*/}
        mode=$(stat -c '%a' "$p"); mode=$(printf '%03o' "0$mode")
        if [ -L "$p" ]; then printf '%s%s l--%s %s %s %s\n' "$ind" "$name" "$mode" "$(stat -c %u "$p")" "$(stat -c %g "$p")" "$(readlink "$p")"
        elif [ -d "$p" ]; then printf '%s%s d--%s %s %s\n' "$ind" "$name" "$mode" "$(stat -c %u "$p")" "$(stat -c %g "$p")"; proto "$p" "$ind  "; printf '%s$\n' "$ind"
        else printf '%s%s ---%s %s %s %s\n' "$ind" "$name" "$mode" "$(stat -c %u "$p")" "$(stat -c %g "$p")" "$p"; fi
    done < <(find "$dir" -mindepth 1 -maxdepth 1 -print0 | sort -z)
}
# Protofile names cannot contain spaces.
mv "$tree/Ümlaut äöü ß.txt" "$tree/Ümlaut_äöü_ß.txt"
{ echo /dev/null; echo '0 0'; echo 'd--755 0 0'; proto "$tree" ' '; echo '$'; } > "$WORK/proto.txt"
truncate -s "$size" "$WORK/xfs.fs"
mkfs.xfs -q -f -L flrxfs -i nrext64=1 -m bigtime=1 -p "$WORK/proto.txt" "$WORK/xfs.fs"
xfs_db -r -c 'version' "$WORK/xfs.fs" > "$OUT/xfs-nrext64.features.txt"
dd if="$WORK/xfs.fs" of="$img" bs=1M seek=$((off / 1048576)) conv=notrunc,sparse status=none
rm "$WORK/xfs.fs"
manifest "$tree" "$OUT/xfs-nrext64.p1.manifest"
# mkfs protofiles do not keep timestamps: no mtime check for this image.
awk 'BEGIN { FS = OFS = "\t" } { $7 = "-"; print }' "$OUT/xfs-nrext64.p1.manifest" > "$WORK/m.tmp" && mv "$WORK/m.tmp" "$OUT/xfs-nrext64.p1.manifest"
finish "$img" xfs-nrext64

# ---------------------------------------------------------------- XFS kernel-written (nrext64=0: fragmentation, unwritten extents)
img=$WORK/xfs-gpt.img
truncate -s 320M "$img"
printf 'label: gpt\nstart=2048, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="data"\n' | sfdisk -q "$img"
read -r off size < <(part "$img" 1)
dev=$(loop "$img" "$off" "$size")
mkfs.xfs -q -f -L flrxfs -i nrext64=0 "$dev"
mountfs "$dev" "$WORK/m"
common_tree "$WORK/m"; linux_extras "$WORK/m"; prealloc "$WORK/m"
for i in $(seq 0 63); do   # two files written in turns with sync: many extents each
    pattern "$WORK/c" 65536 "frag-a-$i"; cat "$WORK/c" >> "$WORK/m/frag-a.bin"; sync
    pattern "$WORK/c" 65536 "frag-b-$i"; cat "$WORK/c" >> "$WORK/m/frag-b.bin"; sync
done
xfs_bmap "$WORK/m/frag-a.bin" > "$OUT/xfs-gpt.frag-a.bmap.txt"
manifest "$WORK/m" "$OUT/xfs-gpt.p1.manifest"
xfs_info "$WORK/m" > "$OUT/xfs-gpt.features.txt"
unmount "$WORK/m"
finish "$img" xfs-gpt

# ---------------------------------------------------------------- LVM: VG over two disks, linear LVs (ext4 root + XFS data spanning both PVs)
img0=$WORK/lvm-disk0.img; img1=$WORK/lvm-disk1.img
truncate -s 256M "$img0"; truncate -s 448M "$img1"
printf 'label: dos\nstart=2048, type=8e\n' | sfdisk -q "$img0"
read -r off size < <(part "$img0" 1)
pv0=$(loop "$img0" "$off" "$size"); pv1=$(loop "$img1")
pvcreate -q -y --config "$LVMCONF" "$pv0" "$pv1"
vgcreate -q --config "$LVMCONF" vgflr "$pv0" "$pv1"
lvcreate -q --config "$LVMCONF" -n root -L 180M vgflr "$pv0"
lvcreate -q --config "$LVMCONF" -n data -L 480M vgflr
lvs --config "$LVMCONF" --segments -o lv_name,seg_start,seg_size,devices vgflr > "$OUT/lvm.segments.txt"
vgmknodes --config "$LVMCONF" vgflr
mke2fs -q -F -t ext4 -L flrroot /dev/vgflr/root
mkfs.xfs -q -f -L flrdata -i nrext64=0 /dev/vgflr/data
datauuid=$(blkid -s UUID -o value /dev/vgflr/data)
mountfs /dev/vgflr/root "$WORK/m"
mkdir -p "$WORK/m/etc"; printf 'root on lvm\n' > "$WORK/m/hello.txt"
printf '/dev/mapper/vgflr-root / ext4 defaults 0 1\nUUID=%s /data xfs defaults 0 2\n' "$datauuid" > "$WORK/m/etc/fstab"
manifest "$WORK/m" "$OUT/lvm.root.manifest"; unmount "$WORK/m"
mountfs /dev/vgflr/data "$WORK/m"
common_tree "$WORK/m"
manifest "$WORK/m" "$OUT/lvm.data.manifest"; unmount "$WORK/m"
vgchange -an -q --config "$LVMCONF" vgflr
finish "$img0" lvm-disk0
finish "$img1" lvm-disk1

[ -n "${OWNER:-}" ] && chown -R "$OWNER" "$OUT"
echo done
