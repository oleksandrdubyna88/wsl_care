#!/bin/sh
# READ-ONLY (S7b.2 plan review, 2026-10-10): for every interop relay of a Windows .exe — its ancestry (pid, comm, uid,
# session id = stat field 6), its fd 0/1/2 link targets, and for each pipe or socket the other holders readable as this
# user; for a socket also its PEER inode from `ss -xpn`. Reads /proc and ss only; prints program base names, never an argument.
echo "now_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ) me=$(id -u)"
field() { rest=${1##*) }; echo "$rest" | awk -v n="$2" '{print $(n-2)}'; }
chain() {
  p=$1
  out=""
  while [ -n "$p" ] && [ "$p" != 0 ]; do
    st=$(cat "/proc/$p/stat" 2>/dev/null) || { out="$out <$p:unreadable>"; break; }
    c=$(cat "/proc/$p/comm" 2>/dev/null)
    u=$(awk '/^Uid:/ {print $2}' "/proc/$p/status" 2>/dev/null)
    s=$(field "$st" 6)
    out="$out <$p:$c:uid$u:sid$s>"
    p=$(field "$st" 4)
  done
  echo "$out"
}
for d in /proc/[0-9]*; do
  pid=${d#/proc/}
  [ "$(readlink "$d/exe" 2>/dev/null)" = /init ] || continue
  prog=$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null | sed -n 2p)
  case "$prog" in
    *.exe|*.EXE) ;;
    *) continue ;;
  esac
  st=$(cat "$d/stat" 2>/dev/null) || continue
  echo "relay pid=$pid prog=$(basename "$prog") sid=$(field "$st" 6) pgrp=$(field "$st" 5) tty=$(field "$st" 7)"
  echo "  chain:$(chain "$pid")"
  for fd in 0 1 2; do
    l=$(readlink "$d/fd/$fd" 2>/dev/null)
    echo "  fd$fd=$l"
    case "$l" in
      pipe:*|socket:*)
        h=""
        for q in /proc/[0-9]*; do
          [ "$q" = "$d" ] && continue
          for f in "$q"/fd/*; do
            [ "$(readlink "$f" 2>/dev/null)" = "$l" ] && { h="$h ${q#/proc/}($(cat "$q/comm" 2>/dev/null))"; break; }
          done
        done
        echo "    other readable holders:${h:- none}"
        ;;
    esac
    case "$l" in
      socket:*)
        ino=$(echo "$l" | tr -dc 0-9)
        echo "    ss: $(ss -xpn 2>/dev/null | awk -v i="$ino" '$6==i || $8==i' | sed 's/  */ /g' | head -2)"
        ;;
    esac
  done
done
echo "end"
