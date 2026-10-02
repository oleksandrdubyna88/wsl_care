#!/bin/bash
# Read-only journal analysis over the last ~2 months.
S() { echo; echo "===== $1 ====="; }
SINCE="2026-08-01"
J="journalctl --no-pager -q --since $SINCE"
S "journal span"; journalctl --no-pager -q -o short-iso | head -1; journalctl --no-pager -q -o short-iso -r | head -1
S "journal config"; grep -v '^#' /etc/systemd/journald.conf | grep . ; ls /etc/systemd/journald.conf.d/ 2>/dev/null; ls -la /var/log/journal 2>/dev/null | head
S "boots count"; journalctl --list-boots --no-pager 2>/dev/null | wc -l
S "boots per day"; journalctl --list-boots --no-pager 2>/dev/null | awk 'NR>1{print $4}' | sort | uniq -c | tail -70
S "boot lengths (first..last)"; journalctl --list-boots --no-pager 2>/dev/null | tail -60
S "OOM / memory kills"; $J -k -g 'Out of memory|oom-kill|oom_reaper|Killed process|invoked oom-killer' -o short-iso | tail -60
S "OOM count by day"; $J -k -g 'Killed process' -o short-iso | cut -c1-10 | sort | uniq -c
S "OOM victims"; $J -k -g 'Killed process' | sed -E 's/.*\(([^)]+)\).*/\1/' | sort | uniq -c | sort -rn | head -20
S "systemd-oomd"; $J -u systemd-oomd | tail -20
S "hung tasks / soft lockups / rcu stalls"; $J -k -g 'hung_task|blocked for more than|soft lockup|rcu_sched|rcu: INFO|watchdog' -o short-iso | tail -30
S "9p / plan9 / drvfs errors"; $J -k -g '9p|p9_|v9fs|drvfs' -o short-iso | tail -20
S "hv / balloon / memory hotplug"; $J -k -g 'hv_balloon|balloon|memory hot' -o short-iso | tail -20
S "WSL init msgs"; $J -g 'WSL|wsl' -o short-iso | grep -iv snap | grep -iE 'error|fail|timeout|WaitForBootProcess' | tail -30
S "failed units over period"; $J -g 'Failed with result|failed with result|entered failed state' -o short-iso | sed -E 's/^[^ ]+ [^ ]+ //' | sed -E 's/\[[0-9]+\]//' | sort | uniq -c | sort -rn | head -25
S "top log emitters"; $J -o json --output-fields=SYSLOG_IDENTIFIER,_COMM 2>/dev/null | grep -oE '"(SYSLOG_IDENTIFIER)":"[^"]+"' | sort | uniq -c | sort -rn | head -25
S "snapd refresh / errors"; $J -u snapd -g 'error|cannot|refresh' -o short-iso | tail -15
S "tmpfiles slow boot"; $J -u systemd-tmpfiles-setup -o short-iso | tail -10
S "slow boot"; $J -g 'WaitForBootProcess|failed to start within' -o short-iso | tail -15
S "docker desktop proxy"; $J -g 'docker-desktop' -o short-iso | grep -iE 'error|fail' | tail -10
S "sar/atop present?"; command -v sar atop vmstat iotop; ls /var/log/sysstat 2>/dev/null | head
S "last shutdown reasons"; $J -g 'Powering off|Shutting down|Reached target.*Shutdown|received SIGTERM|Received SIGRTMIN' -o short-iso | cut -c1-120 | tail -20
