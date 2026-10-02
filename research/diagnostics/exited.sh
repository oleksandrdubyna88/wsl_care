#!/bin/bash
# Exited/created containers: age buckets and anonymous-volume size attached to them (read-only).
docker system df -v --format '{{json .Volumes}}' > /tmp/v.json
python3 - <<'PY'
import json, re, subprocess, datetime
sizes = {v['Name']: v['Size'] for v in json.load(open('/tmp/v.json'))}
def gb(s):
    m = re.match(r'([\d.]+)([kMGT]?B)', s or '0B')
    return float(m.group(1)) * {'B':1e-9,'kB':1e-6,'MB':1e-3,'GB':1,'TB':1000}[m.group(2)] if m else 0
ids = subprocess.run(['docker','ps','-aq','--filter','status=exited','--filter','status=created'],capture_output=True,text=True).stdout.split()
now = datetime.datetime.now(datetime.timezone.utc)
rows = []
for c in json.loads(subprocess.run(['docker','inspect',*ids],capture_output=True,text=True).stdout):
    fin = c['State'].get('FinishedAt','')[:19]
    created = c['Created'][:19]
    t = fin if not fin.startswith('0001') else created
    age = (now - datetime.datetime.fromisoformat(t).replace(tzinfo=datetime.timezone.utc)).days
    anon = [m['Name'] for m in c.get('Mounts',[]) if m.get('Type')=='volume' and re.fullmatch(r'[0-9a-f]{64}', m.get('Name',''))]
    named = [m['Name'] for m in c.get('Mounts',[]) if m.get('Type')=='volume' and m.get('Name') and m['Name'] not in anon]
    rows.append((age, c['Name'].lstrip('/'), sum(gb(sizes.get(a)) for a in anon), named))
old = [r for r in rows if r[0] >= 7]
print(f"exited/created total: {len(rows)}, anon volume data {sum(r[2] for r in rows):.1f} GB")
print(f"  stopped >= 7 days: {len(old)}, anon volume data {sum(r[2] for r in old):.1f} GB")
print(f"  stopped <  7 days: {len(rows)-len(old)}, anon volume data {sum(r[2] for r in rows if r[0]<7):.1f} GB")
print("with NAMED volumes (data survives container removal):")
for r in rows:
    if r[3]: print(f"  {r[1]} ({r[0]} d): {', '.join(r[3])}")
PY
