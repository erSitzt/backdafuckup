#!/bin/sh
# backdafuckup file restore into a Linux guest: applies what the backup server staged over SFTP.
# Runs as root (through sudo, or for a root login directly). POSIX sh and coreutils/busybox only.
#
#   sh restore.sh STAGE MODE OWNER ROOT64
#     STAGE   folder with the numbered files, "manifest" and "links" (/var/tmp/bdfu-<hex>)
#     MODE    overwrite | keep (keep writes RESTORED-<name> next to an existing file)
#     OWNER   1: owner and group from the backup; 0: those of the restore folder
#     ROOT64  the restore folder (base64); nothing is written outside it
#
# Entries, one per line, fields separated by single spaces, every name base64:
#   D idx path                                  folder (an existing folder is fine)
#   F idx path n mode uid gid mtime atime       file STAGE/n; times as [[CC]YY]MMDDhhmm.SS in UTC
#   M idx path mode uid gid mtime atime         times, owner and mode of a folder (after its content)
#   L idx path target uid gid                   symbolic link (file "links", applied last)
# Output lines: OK idx | RENAMED idx path64 | FAIL idx reason64 | PARTIAL idx reason64, finally DONE.
#
# Names from the backup only ever reach commands as quoted arguments after "--", never as shell code.

stage=$1
mode=$2
owner=$3
export LC_ALL=C TZ=UTC0
umask 077

# _d = decoded $1; a trailing "x" keeps trailing newlines that command substitution would drop.
decode() {
  _d=$(printf '%s' "$1" | base64 -d 2>/dev/null && printf x) || return 1
  _d=${_d%x}
}

enc() { printf '%s' "$1" | base64 | tr -d '\n'; }
ok() { echo "OK $1"; }
fail() { echo "FAIL $1 $(enc "$2")"; }
partial() { echo "PARTIAL $1 $(enc "$2")"; }

decode "$4" || { echo "FAIL 0 $(enc "the restore folder cannot be decoded")"; echo DONE; exit 0; }
root=${_d%/}
[ -n "$root" ] || root=/

selinux=
if command -v restorecon >/dev/null 2>&1 && [ -e /sys/fs/selinux/enforce ]; then selinux=1; fi

# The owner for "OWNER 0": that of the restore folder.
folder_owner=$(stat -c '%u:%g' -- "$root" 2>/dev/null || echo 0:0)

# Absolute, below the restore folder, without "." or ".." components.
inside() {
  case $1 in
    /*) ;;
    *) why="not an absolute path"; return 1 ;;
  esac
  case "$1/" in
    */./* | */../*) why="relative components in the path"; return 1 ;;
  esac
  if [ "$root" != / ]; then
    case $1 in
      "$root" | "$root"/*) ;;
      *) why="outside the restore folder $root"; return 1 ;;
    esac
  fi
  return 0
}

# Creates the folders above $1 as needed; a symbolic link or a non-folder in the way is an error.
parents() {
  _rest=${1#/}
  _cur=
  while :; do
    case $_rest in
      */*) _c=${_rest%%/*}; _rest=${_rest#*/} ;;
      *) return 0 ;;
    esac
    [ -n "$_c" ] || continue
    _cur=$_cur/$_c
    if [ -L "$_cur" ]; then
      why="$_cur is a symbolic link; nothing is written through it"
      return 1
    fi
    if [ -e "$_cur" ]; then
      [ -d "$_cur" ] || { why="$_cur is not a folder"; return 1; }
    else
      mkdir -- "$_cur" 2>/dev/null || { why="$_cur cannot be created"; return 1; }
      chmod 755 -- "$_cur" 2>/dev/null
    fi
  done
}

# _free = RESTORED-<name>, then RESTORED-<stem> (2)<ext>, ... in the same folder.
free_name() {
  _dir=${1%/*}
  _base=RESTORED-${1##*/}
  case ${1##*/} in
    ?*.*) _stem=${_base%.*}; _ext=.${_base##*.} ;;
    *) _stem=$_base; _ext= ;;
  esac
  _free=$_dir/$_base
  _i=2
  while [ -e "$_free" ] || [ -L "$_free" ]; do
    [ "$_i" -le 999 ] || return 1
    _free="$_dir/$_stem ($_i)$_ext"
    _i=$((_i + 1))
  done
  return 0
}

# Owner, mode and times of $1 ($2 mode, $3 uid, $4 gid, $5 mtime, $6 atime; "-" = unknown); sets problem.
apply_meta() {
  problem=
  if [ "$owner" = 1 ] && [ "$3" != - ] && [ "$4" != - ]; then
    chown -h -- "$3:$4" "$1" 2>/dev/null || problem="owner $3:$4 not set"
  else
    chown -h -- "$folder_owner" "$1" 2>/dev/null || problem="owner not set"
  fi
  [ "$2" = - ] || chmod -- "$2" "$1" 2>/dev/null || problem="${problem:+$problem, }mode $2 not set"
  [ "$5" = - ] || touch -m -t "$5" -- "$1" 2>/dev/null || problem="${problem:+$problem, }modification time not set"
  [ "$6" = - ] || touch -a -t "$6" -- "$1" 2>/dev/null || problem="${problem:+$problem, }access time not set"
}

# The SELinux label the policy gives the final name (where SELinux is on).
label() {
  [ -z "$selinux" ] || restorecon -F -- "$1" 2>/dev/null || problem="${problem:+$problem, }SELinux label not set"
}

put_file() { # idx path n mode uid gid mtime atime
  inside "$2" || { fail "$1" "$why"; return; }
  parents "$2" || { fail "$1" "$why"; return; }
  target=$2
  if [ -L "$target" ]; then fail "$1" "a symbolic link with this name exists; it is not replaced"; return; fi
  if [ -d "$target" ]; then fail "$1" "a folder with this name exists"; return; fi
  if [ -e "$target" ] && [ "$mode" = keep ]; then
    free_name "$target" || { fail "$1" "no free RESTORED- name"; return; }
    target=$_free
  fi
  dir=${target%/*}
  [ -n "$dir" ] || dir=/
  tmp=$dir/.bdfu-restore-$1-$$
  if ! err=$(cp -- "$stage/$3" "$tmp" 2>&1); then
    rm -f -- "$tmp"
    fail "$1" "copying failed: $err"
    return
  fi
  apply_meta "$tmp" "$4" "$5" "$6" "$7" "$8"
  if ! err=$(mv -f -- "$tmp" "$target" 2>&1); then
    rm -f -- "$tmp"
    fail "$1" "replacing failed: $err"
    return
  fi
  rm -f -- "$stage/$3"
  label "$target"
  if [ -n "$problem" ]; then
    partial "$1" "$problem"
  fi
  if [ "$target" != "$2" ]; then echo "RENAMED $1 $(enc "$target")"; else ok "$1"; fi
}

make_dir() { # idx path
  inside "$2" || { fail "$1" "$why"; return; }
  parents "$2" || { fail "$1" "$why"; return; }
  if [ -L "$2" ]; then
    fail "$1" "a symbolic link with this name exists; nothing is written through it"
  elif [ -d "$2" ]; then
    :
  elif [ -e "$2" ]; then
    fail "$1" "a file with this name exists"
  elif ! err=$(mkdir -- "$2" 2>&1); then
    fail "$1" "$err"
  fi
}

folder_meta() { # idx path mode uid gid mtime atime
  inside "$2" || return
  [ -d "$2" ] && [ ! -L "$2" ] || return
  apply_meta "$2" "$3" "$4" "$5" "$6" "$7"
  label "$2"
  [ -z "$problem" ] || partial "$1" "$problem"
}

make_link() { # idx path target uid gid
  inside "$2" || { fail "$1" "$why"; return; }
  parents "$2" || { fail "$1" "$why"; return; }
  path=$2
  if [ -d "$path" ] && [ ! -L "$path" ]; then fail "$1" "a folder with this name exists"; return; fi
  if { [ -e "$path" ] || [ -L "$path" ]; } && [ "$mode" = keep ]; then
    free_name "$path" || { fail "$1" "no free RESTORED- name"; return; }
    path=$_free
  fi
  tmp=${path%/*}/.bdfu-restore-$1-$$
  if ! err=$(ln -s -- "$3" "$tmp" 2>&1) || ! err=$(mv -f -- "$tmp" "$path" 2>&1); then
    rm -f -- "$tmp"
    fail "$1" "$err"
    return
  fi
  if [ "$owner" = 1 ] && [ "$4" != - ]; then chown -h -- "$4:$5" "$path" 2>/dev/null; fi
  if [ "$path" != "$2" ]; then echo "RENAMED $1 $(enc "$path")"; else ok "$1"; fi
}

# Folders and files first, then the links, then the folder times (creating anything in a folder changes them).
while read -r kind idx a b c d e f g; do
  [ "$kind" != M ] || continue
  decode "$a" || { fail "$idx" "damaged manifest entry"; continue; }
  case $kind in
    D) make_dir "$idx" "$_d" ;;
    F) put_file "$idx" "$_d" "$b" "$c" "$d" "$e" "$f" "$g" ;;
    *) fail "$idx" "unknown entry $kind" ;;
  esac
done < "$stage/manifest"

if [ -f "$stage/links" ]; then
  while read -r kind idx a b c d; do
    decode "$a" || { fail "$idx" "damaged manifest entry"; continue; }
    name=$_d
    decode "$b" || { fail "$idx" "damaged manifest entry"; continue; }
    make_link "$idx" "$name" "$_d" "$c" "$d"
  done < "$stage/links"
fi

while read -r kind idx a b c d e f g; do
  [ "$kind" = M ] || continue
  decode "$a" || { fail "$idx" "damaged manifest entry"; continue; }
  folder_meta "$idx" "$_d" "$b" "$c" "$d" "$e" "$f"
done < "$stage/manifest"

case $stage in
  /var/tmp/bdfu-*) rm -rf -- "$stage" ;;
esac
echo DONE
