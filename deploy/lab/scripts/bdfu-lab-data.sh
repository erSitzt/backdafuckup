#!/bin/sh
# Test data of the lab runbook (docs/lab-runbook.md, "Preparation"), run once by cloud-init as root.
# Usage: bdfu-lab-data.sh <folder> [<mount point of a data disk> ...]
# The first folder is always filled; the others only when a file system is mounted there.
# For each folder, /root/bdfu-lab-before<folder with _>.txt holds the SHA-256 of every file and
# /root/bdfu-lab-meta-before<folder with _>.txt owner, mode, time and link target of every entry.
set -eu

fill() {
    dir=$1
    mkdir -p "$dir/tree/sub"
    dd if=/dev/urandom of="$dir/tree/data.bin" bs=1M count=20 status=none
    echo owned > "$dir/tree/sub/owned.txt"
    chown 1234:1234 "$dir/tree/sub/owned.txt"
    chmod 0640 "$dir/tree/sub/owned.txt"
    ln -sfn ../data.bin "$dir/tree/sub/link"
    # A name that must never run as a command on restore (check D6).
    # shellcheck disable=SC2016 # literal on purpose
    : > "$dir/tree/"'$(touch HACKED) and spaces.txt'
    printf 'umlauts and spaces\n' > "$dir/tree/Grüße mit Leerzeichen.txt"
    touch -d '2020-01-02 03:04:05' "$dir/tree/data.bin"
    name=$(echo "$dir" | tr / _)
    find "$dir" -type f -exec sha256sum {} + | sort -k2 > "/root/bdfu-lab-before$name.txt"
    find "$dir" -printf '%p %u:%g %m %Ts %l\n' | sort > "/root/bdfu-lab-meta-before$name.txt"
    echo "bdfu-lab-data: test data in $dir"
}

first=1
for dir in "$@"; do
    if [ "$first" = 1 ] || mountpoint -q "$dir"; then
        fill "$dir"
    fi
    first=0
done

# SELinux label check (D5) on systems where SELinux is on.
if command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; then
    mkdir -p /var/www/html/bdfu-lab
    echo page > /var/www/html/bdfu-lab/index.html
    restorecon -R /var/www/html
    ls -Z /var/www/html/bdfu-lab/index.html
fi
