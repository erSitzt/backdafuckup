#!/bin/sh
# Lab VM setup (backdafuckup deploy/lab), run once by cloud-init as root. It puts the empty data disks of the VM to use,
# with the mkfs defaults of this distribution, so the file-level restore reads what a current installation writes:
#   one empty disk:      a GPT partition with XFS, mounted by UUID on /srv/bdfu-xfs
#   two or more disks:   one LVM volume group "bdfulab" over all of them, the first PV on a GPT partition and the
#                        others on whole disks (both forms occur in practice), and one linear logical volume with
#                        XFS that spans every disk, mounted as /dev/mapper/bdfulab-data on /srv/bdfu-lvm
# Disks with partitions, a file system or any other signature are never touched.
set -eu

log() { echo "bdfu-lab-disks: $*"; }

need() {
    command -v "$1" >/dev/null 2>&1 && return 0
    log "installing $2 for $1"
    if command -v dnf >/dev/null 2>&1; then dnf -y -q install "$2"; else apt-get -q update && apt-get -y -q install "$2"; fi
}

empty_disks() {
    lsblk -dpno NAME,TYPE | while read -r name type; do
        [ "$type" = disk ] || continue
        [ "$(lsblk -no NAME "$name" | wc -l)" -eq 1 ] || continue           # no partitions
        [ -z "$(blkid -p -o value -s TYPE "$name" 2>/dev/null || true)" ] || continue   # no signature
        [ -z "$(blkid -p -o value -s PTTYPE "$name" 2>/dev/null || true)" ] || continue # no partition table
        echo "$name"
    done
}

# Creates one GPT partition of the given type over the disk and prints its device name.
partition() {
    printf 'label: gpt\ntype=%s\n' "$2" | sfdisk -q "$1" >/dev/null
    udevadm settle 2>/dev/null || true
    lsblk -lpno NAME,TYPE "$1" | awk '$2 == "part" { print $1; exit }'
}

mount_xfs() {
    mkdir -p "$3"
    echo "$1 $3 xfs defaults,nofail 0 2" >> /etc/fstab
    mount "$3"
    log "$2 mounted on $3 ($1)"
}

disks=$(empty_disks)
count=$(echo "$disks" | grep -c . || true)
log "empty data disks: ${count} ${disks}"
need mkfs.xfs xfsprogs

case "$count" in
    0) ;;
    1)
        part=$(partition "$disks" 0FC63DAF-8483-4772-8E79-3D69D8477DE4)
        mkfs.xfs -q "$part"
        mount_xfs "UUID=$(blkid -o value -s UUID "$part")" "$part" /srv/bdfu-xfs
        ;;
    *)
        need pvcreate lvm2
        first=$(echo "$disks" | head -n 1)
        volumes="$(partition "$first" E6D6D379-F507-44C2-A23C-238F2A3DF928) $(echo "$disks" | tail -n +2 | tr '\n' ' ')"
        # shellcheck disable=SC2086 # one word per physical volume
        pvcreate -q -y $volumes
        # shellcheck disable=SC2086
        vgcreate -q bdfulab $volumes
        lvcreate -q -y -n data -l 100%FREE bdfulab
        mkfs.xfs -q /dev/mapper/bdfulab-data
        mount_xfs /dev/mapper/bdfulab-data "LV bdfulab/data over $volumes" /srv/bdfu-lvm
        ;;
esac

xfs_info /srv/bdfu-xfs 2>/dev/null | head -n 4 || true
xfs_info /srv/bdfu-lvm 2>/dev/null | head -n 4 || true
