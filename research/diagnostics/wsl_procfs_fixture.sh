#!/bin/sh
# Read-only capture of a procfs/cgroup fixture tree for wsl_care E2.S1.
# Writes ONLY under /tmp/wslcare-fixture-capture, streams a tar of it to stdout, then removes it.
set -u
OUT=/tmp/wslcare-fixture-capture
rm -rf "$OUT"
mkdir -p "$OUT/proc/pressure" "$OUT/sys/fs/cgroup/docker" "$OUT/etc"
for f in meminfo buddyinfo stat uptime mounts loadavg; do cat /proc/$f > "$OUT/proc/$f" 2>/dev/null; done
for f in memory io cpu; do cat /proc/pressure/$f > "$OUT/proc/pressure/$f" 2>/dev/null; done
# top 40 by RSS, plus pid 1, plus any process whose cwd or cmdline is under /mnt/
PIDS=$( (ps -eo pid= --sort=-rss | head -40; echo 1;
  for p in /proc/[0-9]*; do n=${p#/proc/}; c=$(readlink "$p/cwd" 2>/dev/null); a=$(tr '\0' ' ' < "$p/cmdline" 2>/dev/null);
    case "$c $a" in */mnt/*) echo "$n";; esac; done) | tr -d ' ' | sort -un)
: > "$OUT/links.txt"
for n in $PIDS; do
  p=/proc/$n
  [ -d "$p" ] || continue
  mkdir -p "$OUT/proc/$n"
  for f in status stat cgroup cmdline comm; do cat "$p/$f" > "$OUT/proc/$n/$f" 2>/dev/null; done
  t=$(readlink "$p/cwd" 2>/dev/null) && printf 'proc/%s/cwd\t%s\n' "$n" "$t" >> "$OUT/links.txt"
done
for d in /sys/fs/cgroup/docker/*/; do
  id=$(basename "$d")
  case "$id" in *[!0-9a-f]*) continue;; esac
  mkdir -p "$OUT/sys/fs/cgroup/docker/$id"
  for f in memory.current memory.stat cgroup.procs; do cat "$d/$f" > "$OUT/sys/fs/cgroup/docker/$id/$f" 2>/dev/null; done
done
cat /sys/fs/cgroup/docker/memory.current > "$OUT/sys/fs/cgroup/docker/memory.current"
# passwd: name:uid only, for the uids that appear
for u in $(cat "$OUT"/proc/[0-9]*/status | awk '/^Uid:/{print $2}' | sort -un); do getent passwd "$u" | awk -F: '{print $1":x:"$3":"$4"::"$6":"$7}'; done > "$OUT/etc/passwd"
{ date -u +%Y-%m-%dT%H:%M:%SZ; uname -r; getconf CLK_TCK; getconf PAGESIZE; df -B1 --output=size,used,avail,target /; } > "$OUT/capture-info.txt"
tar -C "$OUT" -cf - .
rm -rf "$OUT"
