#!/bin/bash
# Analyse rsyslog history (/var/log/syslog*, kern.log*) — read-only.
S() { echo; echo "===== $1 ====="; }
ALL() { for f in $(ls -tr /var/log/syslog* 2>/dev/null); do case "$f" in *.gz) zcat "$f";; *) cat "$f";; esac; done; }
KALL() { for f in $(ls -tr /var/log/kern.log* 2>/dev/null); do case "$f" in *.gz) zcat "$f";; *) cat "$f";; esac; done; }
ALL > /tmp/sys_all.log 2>/dev/null; KALL > /tmp/kern_all.log 2>/dev/null
S "span"; head -1 /tmp/sys_all.log | cut -c1-40; tail -1 /tmp/sys_all.log | cut -c1-40; wc -l /tmp/sys_all.log /tmp/kern_all.log
S "lines per day"; cut -c1-10 /tmp/sys_all.log | sort | uniq -c
S "top emitters (all period)"; awk '{print $3}' /tmp/sys_all.log | sed -E 's/\[[0-9]+\]//; s/:$//' | sort | uniq -c | sort -rn | head -20
S "top emitters on the biggest day"; D=$(cut -c1-10 /tmp/sys_all.log | sort | uniq -c | sort -rn | head -1 | awk '{print $2}'); echo "$D"; grep -a "^$D" /tmp/sys_all.log | awk '{print $3}' | sed -E 's/\[[0-9]+\]//; s/:$//' | sort | uniq -c | sort -rn | head -10
S "sample of top emitter on big day"; E=$(grep -a "^$D" /tmp/sys_all.log | awk '{print $3}' | sed -E 's/\[[0-9]+\]//; s/:$//' | sort | uniq -c | sort -rn | head -1 | awk '{print $2}'); grep -a "^$D" /tmp/sys_all.log | grep -a " $E" | cut -c1-260 | awk 'NR%2000==1' | head -8
S "clock jumps per day"; grep -a -E 'Time jumped backwards|Clock change detected|Time has been changed' /tmp/sys_all.log | cut -c1-10 | sort | uniq -c
S "OOM / hung / lockup (kern)"; grep -a -E 'Out of memory|oom-kill|Killed process|invoked oom-killer|blocked for more than|soft lockup|hung_task|rcu.*stall' /tmp/kern_all.log | cut -c1-220 | tail -40
S "OOM count by day"; grep -a -E 'Killed process|oom-kill:' /tmp/kern_all.log | cut -c1-10 | sort | uniq -c
S "OOM victims"; grep -a -E 'Killed process' /tmp/kern_all.log | sed -E 's/.*Killed process [0-9]+ \(([^)]+)\).*/\1/' | sort | uniq -c | sort -rn | head
S "balloon / memory events (kern)"; grep -a -iE 'hv_balloon|page allocation failure|memory: usage|cgroup.*memory' /tmp/kern_all.log | grep -a -v registering | cut -c1-200 | tail -20
S "9p / drvfs errors (kern)"; grep -a -iE '9p|v9fs|drvfs' /tmp/kern_all.log | grep -a -viE 'Installing' | cut -c1-200 | sort | uniq -c | sort -rn | head -10
S "ENOBUFS / no buffer space"; grep -a -iE 'no buffer space|ENOBUFS|Too many open files|inotify' /tmp/sys_all.log | sed -E 's/^[^ ]+ //' | cut -c1-160 | sed -E 's/\[[0-9]+\]//' | sort | uniq -c | sort -rn | head -15
S "failed units (period)"; grep -a -E 'Failed with result|failed with result' /tmp/sys_all.log | sed -E 's/^[^ ]+ [^ ]+ //; s/\[[0-9]+\]//' | sort | uniq -c | sort -rn | head -20
S "WSL errors (period)"; grep -a -E 'WSL \(' /tmp/sys_all.log | grep -a ERROR | sed -E 's/^[^ ]+ [^ ]+ [^ ]+ //; s/\([0-9]+( - [^)]*)?\)//; s/ChildPid=[0-9]+//; s/[0-9]+ms/Nms/' | cut -c1-140 | sort | uniq -c | sort -rn | head -15
S "boot slowness per day"; grep -a -E 'failed to start within' /tmp/sys_all.log | cut -c1-10 | sort | uniq -c
S "Startup finished lines"; grep -a -E 'Startup finished in' /tmp/sys_all.log | cut -c1-10,30-200 | tail -25
S "binary garbage (NUL runs = unclean shutdown)"; grep -a -c -P '\x00' /tmp/sys_all.log
S "vscode server: what it logs (sample)"; grep -a 'Microsoft.VisualStudio.Code.Server' /tmp/sys_all.log | sed -E 's/^[^ ]+ [^ ]+ [^ ]+ //' | cut -c1-90 | sed -E 's/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -12
S "wsl-pro-service per day"; grep -a 'wsl-pro-service' /tmp/sys_all.log | cut -c1-10 | sort | uniq -c | tail -40
S "vscode server lines per day"; grep -a -E 'Microsoft.VisualStudio.Code.Server|code-server|vscode-server' /tmp/sys_all.log | cut -c1-10 | sort | uniq -c | tail -40
S "docker/containerd messages per day"; grep -a -iE 'docker|containerd' /tmp/sys_all.log | cut -c1-10 | sort | uniq -c | tail -40
rm -f /tmp/sys_all.log /tmp/kern_all.log
