#!/bin/bash
# Read-only "before" snapshot for the one-time cleanup.
S() { echo; echo "===== $1 ====="; }
S "time"; date -Is; uptime
S "memory"; free -m; grep -E 'MemAvailable|^Cached|Inactive\(anon\)|SwapFree' /proc/meminfo; cat /proc/buddyinfo
S "disk"; df -h / /mnt/c | cat
S "docker version"; docker version --format '{{.Server.Version}}' 2>&1
S "docker df"; docker system df 2>&1
S "containers running"; docker ps --format '{{.Names}}\t{{.Image}}\t{{.Status}}' 2>&1 | head -30
S "exited total / testcontainers"; docker ps -a --filter status=exited -q | wc -l; docker ps -a --filter status=exited --filter label=org.testcontainers=true -q | wc -l; docker ps -a --filter status=created -q | wc -l
S "exited non-testcontainers"; docker ps -a --filter status=exited --format '{{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Label "org.testcontainers"}}' | awk -F'\t' '$4!="true"' | head -40
S "volumes dangling: named vs anonymous"; docker volume ls -q --filter dangling=true | awk '{ if ($0 ~ /^[0-9a-f]{64}$/) a++; else { n++; print "named:", $0 } } END { print "anonymous:", a+0, "named:", n+0 }' | tail -40
S "images dangling"; docker images -f dangling=true -q | wc -l
S "build cache"; docker buildx du 2>/dev/null | tail -3
S "active dev processes"; ps -eo pid,etime,rss,comm,args --sort=-rss | grep -E 'dotnet|node|claude|codex|gemini|testhost|VBCSCompiler|MSBuild|python|java' | grep -v grep | cut -c1-200 | head -30
S "logged-in sessions / terminals"; who; ps -eo pid,tty,comm | awk '$2 ~ /pts/' | head -20
S "caches"; du -sh ~/.npm ~/.cache/* ~/.vscode-server/data/* ~/.nuget/packages 2>/dev/null | sort -rh | head -15
S "apt cache"; du -sh /var/cache/apt 2>/dev/null
S "snaps disabled"; snap list --all 2>/dev/null | awk '/disabled/'
S "fstrim / sparse"; lsblk -D 2>/dev/null | head -5
