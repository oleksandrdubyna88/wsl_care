#!/bin/sh
# READ-ONLY (S7b experiment step A, 2026-10-09): every interop relay of creds-mcp.exe — pid, start ticks (stat field 22),
# parent pid, parent comm, uid, and whether the parent is the session init. Reads /proc only; prints only the program's
# base name, never an argument.
echo "now_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ) uptime=$(cut -d' ' -f1 /proc/uptime) me=$(id -u)"
for d in /proc/[0-9]*; do
  pid=${d#/proc/}
  [ "$(readlink "$d/exe" 2>/dev/null)" = /init ] || continue
  prog=$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null | sed -n 2p)
  case "$prog" in
    */creds-mcp.exe) ;;
    *) continue ;;
  esac
  stat=$(cat "$d/stat" 2>/dev/null) || continue
  rest=${stat##*) }
  ppid=$(echo "$rest" | awk '{print $2}')
  ticks=$(echo "$rest" | awk '{print $20}')
  pcomm=$(cat "/proc/$ppid/comm" 2>/dev/null)
  puid=$(awk '/^Uid:/ {print $2}' "/proc/$ppid/status" 2>/dev/null)
  uid=$(awk '/^Uid:/ {print $2}' "$d/status" 2>/dev/null)
  case "$pcomm" in
    Relay\(*\)) client=gone ;;
    *) client=alive ;;
  esac
  echo "relay pid=$pid ticks=$ticks uid=$uid ppid=$ppid parent=$pcomm parent_uid=$puid client=$client prog=$(basename "$prog")"
done
