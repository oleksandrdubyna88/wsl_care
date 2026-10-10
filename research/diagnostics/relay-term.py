#!/usr/bin/env python3
"""S7b experiment, approved by the owner 2026-10-09 (Q-S7b-3): end exactly ONE client-gone creds-mcp.exe interop relay by
pid AND start ticks, SIGTERM only, and time its exit. Identity is held through a pidfd, so the checks and the signal reach
the same process. Refuses (exit 3) on any mismatch. Deletes nothing, writes nothing but stdout.

usage: relay-term.py <pid> <start-ticks>
"""
import os
import re
import select
import signal
import sys
import time

WAIT_SECONDS = 30


def now():
    with open('/proc/uptime') as f:
        up = f.read().split()[0]
    return f"utc={time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime())}.{int(time.time() % 1 * 1000):03d}Z uptime={up}"


def refuse(why):
    print(f"{now()} REFUSED: {why}")
    sys.exit(3)


def stat_of(pid):
    with open(f'/proc/{pid}/stat') as f:
        rest = f.read().rsplit(') ', 1)[1].split()
    return int(rest[1]), int(rest[19])  # ppid, start ticks


def uid_of(pid):
    with open(f'/proc/{pid}/status') as f:
        for line in f:
            if line.startswith('Uid:'):
                return int(line.split()[1])
    return -1


def links_of(pid):
    out = {}
    for fd in ('0', '1', '2'):
        try:
            out[fd] = os.readlink(f'/proc/{pid}/fd/{fd}')
        except OSError:
            out[fd] = ''
    return out


def holders(targets, relay):
    found = []
    for entry in os.listdir('/proc'):
        if not entry.isdigit() or int(entry) == relay:
            continue
        try:
            for fd in os.listdir(f'/proc/{entry}/fd'):
                try:
                    if os.readlink(f'/proc/{entry}/fd/{fd}') in targets:
                        found.append(entry)
                        break
                except OSError:
                    pass
        except OSError:
            pass
    return found


def main():
    pid, ticks = int(sys.argv[1]), int(sys.argv[2])
    print(f"{now()} T0 target relay pid={pid} ticks={ticks} me={os.getuid()}")
    try:
        pidfd = os.pidfd_open(pid)
    except OSError as e:
        refuse(f"pidfd_open: {e}")
    ppid, start = stat_of(pid)
    if start != ticks:
        refuse(f"start ticks now {start}, not {ticks}: another process")
    if os.readlink(f'/proc/{pid}/exe') != '/init':
        refuse("not an interop relay (exe is not /init)")
    with open(f'/proc/{pid}/cmdline', 'rb') as f:
        argv = f.read().split(b'\0')
    if len(argv) < 2 or not argv[1].decode(errors='replace').endswith('/creds-mcp.exe'):
        refuse("not a creds-mcp.exe relay")
    if uid_of(pid) != os.getuid():
        refuse("not this user's process")
    with open(f'/proc/{ppid}/comm') as f:
        pcomm = f.read().strip()
    if not re.fullmatch(r'Relay\(\d+\)', pcomm) or uid_of(ppid) != 0:
        refuse(f"parent {ppid} is '{pcomm}' (uid {uid_of(ppid)}): the client may be alive")
    links = links_of(pid)
    targets = {v for v in links.values() if v.startswith(('pipe:', 'socket:'))}
    others = holders(targets, pid)
    print(f"{now()} checks: parent={ppid} '{pcomm}' uid 0; stdio={links}; other readable holders={others}")
    if others:
        refuse(f"stdio held by {others}: a client may be alive")
    signal.pidfd_send_signal(pidfd, signal.SIGTERM)
    print(f"{now()} T1 SIGTERM sent through the pidfd")
    poller = select.poll()
    poller.register(pidfd, select.POLLIN)
    deadline = time.monotonic() + WAIT_SECONDS
    while time.monotonic() < deadline:
        if poller.poll(100):
            print(f"{now()} T2 relay exited")
            return 0
    print(f"{now()} T2 relay STILL RUNNING after {WAIT_SECONDS} s of SIGTERM (no SIGKILL sent)")
    return 4


if __name__ == '__main__':
    sys.exit(main())
