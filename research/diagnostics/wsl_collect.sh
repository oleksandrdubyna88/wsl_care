#!/bin/bash
# Read-only diagnostics of a WSL distro. Nothing is changed.
S() { echo; echo "===== $1 ====="; }
S "identity"; uname -a; cat /etc/os-release | head -3; whoami; uptime
S "memory"; free -h; cat /proc/meminfo | egrep 'MemTotal|MemFree|MemAvailable|^Cached|Buffers|SwapTotal|SwapFree|Shmem:|Slab|SReclaimable|Dirty'
S "pressure"; cat /proc/pressure/memory /proc/pressure/cpu /proc/pressure/io 2>/dev/null
S "sysctl"; sysctl vm.swappiness vm.vfs_cache_pressure vm.drop_caches vm.min_free_kbytes fs.inotify.max_user_watches fs.inotify.max_user_instances 2>/dev/null
S "wsl.conf"; cat /etc/wsl.conf 2>/dev/null
S "top mem"; ps -eo pid,ppid,user,rss,vsz,pcpu,etime,comm --sort=-rss | head -30
S "top cpu"; ps -eo pid,user,pcpu,rss,etime,comm --sort=-pcpu | head -15
S "process counts"; ps -eo comm | sort | uniq -c | sort -rn | head -25
S "zombies"; ps -eo stat,pid,ppid,comm | awk '$1 ~ /Z/' | head
S "disk"; df -hT | egrep -v 'tmpfs|overlay' | head -20; df -h /tmp /run /dev/shm 2>/dev/null
S "biggest dirs home"; du -xsh ~/* ~/.[!.]* 2>/dev/null | sort -rh | head -25
S "biggest dirs root"; sudo -n du -xsh /var/* /usr/* /tmp /opt /snap 2>/dev/null | sort -rh | head -20 || du -xsh /var/* /tmp /opt 2>/dev/null | sort -rh | head -20
S "caches"; du -sh ~/.cache/* 2>/dev/null | sort -rh | head -15; du -sh ~/.nuget/packages ~/.npm ~/.cargo/registry ~/.rustup ~/.dotnet ~/.local/share/Trash ~/.vscode-server ~/.claude ~/.codex ~/.gemini 2>/dev/null
S "journal"; journalctl --disk-usage 2>/dev/null; journalctl --list-boots --no-pager 2>/dev/null | tail -70
S "failed units"; systemctl --failed --no-pager 2>/dev/null; systemctl --user --failed --no-pager 2>/dev/null
S "services running"; systemctl list-units --type=service --state=running --no-pager 2>/dev/null | head -50
S "timers"; systemctl list-timers --all --no-pager 2>/dev/null | head -30
S "snap"; snap list 2>/dev/null; snap list --all 2>/dev/null | awk '/disabled/'
S "docker in distro"; command -v docker && docker system df 2>/dev/null
S "tmp"; ls -la /tmp | wc -l; du -sh /tmp 2>/dev/null; find /tmp -maxdepth 1 -mtime +7 2>/dev/null | wc -l
S "inotify users"; for p in /proc/[0-9]*; do n=$(ls -l $p/fd 2>/dev/null | grep -c inotify); [ "$n" -gt 0 ] && echo "$n $(cat $p/comm)"; done | sort -rn | head -10
S "open fds top"; for p in /proc/[0-9]*; do n=$(ls $p/fd 2>/dev/null | wc -l); echo "$n $(cat $p/comm 2>/dev/null) $(basename $p)"; done | sort -rn | head -10
S "logs dir"; sudo -n du -sh /var/log/* 2>/dev/null | sort -rh | head -10 || du -sh /var/log/* 2>/dev/null | sort -rh | head -10
