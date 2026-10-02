#!/bin/bash
# One-time SAFE cleanup (plan Phase 0.2, actions A5-A7 + apt/snap). Run as root; docker runs as jinx.
S() { echo; echo "===== $1 ====="; }
D() { sudo -u jinx docker "$@"; }
S "docker server version"; V=$(D version --format '{{.Server.Version}}'); echo "$V"
[ "${V%%.*}" -ge 23 ] || { echo "Docker < 23: volume prune would also take named volumes — abort"; exit 1; }
S "build cache older than 7 days"; D builder prune -f --filter until=168h 2>&1 | tail -2
S "dangling images"; D image prune -f 2>&1 | tail -2
S "anonymous unused volumes (named are kept by default on Docker >= 23)"; D volume prune -f 2>&1 | tail -2
S "named volumes still present"; D volume ls -q | grep -vE '^[0-9a-f]{64}$' | sort
S "apt cache"; apt-get clean && du -sh /var/cache/apt
S "disabled snap revisions"; snap list --all | awk '/disabled/{print $1, $3}' | while read n r; do snap remove "$n" --revision="$r"; done
S "after: docker df"; D system df
S "after: disk"; df -h / | cat
