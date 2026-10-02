#!/bin/bash
# User-approved 2026-10-02: old stopped containers, all build cache, unused images, npm cache. Run as jinx.
S() { echo; echo "===== $1 ====="; }
S "before"; docker system df; du -sh ~/.npm
S "stopped/created containers idle >= 7 days (docker rm -v: anonymous volumes go, named stay)"
python3 - > /tmp/old_ids.txt <<'PY'
import json, subprocess, datetime
ids = subprocess.run(['docker','ps','-aq','--filter','status=exited','--filter','status=created'],capture_output=True,text=True).stdout.split()
now = datetime.datetime.now(datetime.timezone.utc)
for c in json.loads(subprocess.run(['docker','inspect',*ids],capture_output=True,text=True).stdout):
    fin = c['State'].get('FinishedAt','')[:19]
    t = c['Created'][:19] if fin.startswith('0001') or not fin else fin
    if (now - datetime.datetime.fromisoformat(t).replace(tzinfo=datetime.timezone.utc)).days >= 7:
        print(c['Id'][:12], c['Name'].lstrip('/'))
PY
wc -l < /tmp/old_ids.txt
cut -d' ' -f2 /tmp/old_ids.txt | tr '\n' ' '; echo
cut -d' ' -f1 /tmp/old_ids.txt | xargs -r docker rm -v | wc -l
S "all build cache"; docker builder prune -af 2>&1 | tail -1
S "unused images (not referenced by any container)"; docker image prune -af 2>&1 | tail -1
S "npm cache"; bash -ic 'npm cache clean --force' 2>&1 | grep -v -E '^$|no job control|cannot set terminal' ; du -sh ~/.npm
S "after"; docker system df; docker ps -a -q | wc -l; df -h / | cat
