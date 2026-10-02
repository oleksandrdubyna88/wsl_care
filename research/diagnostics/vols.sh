#!/bin/bash
# Size and age of dangling anonymous volumes (read-only).
docker system df -v --format '{{json .Volumes}}' 2>/dev/null > /tmp/vols.json
python3 - <<'PY'
import json, re, subprocess
vols = json.load(open('/tmp/vols.json'))
dangling = set(subprocess.run(['docker','volume','ls','-q','--filter','dangling=true'],capture_output=True,text=True).stdout.split())
def gb(s):
    m = re.match(r'([\d.]+)([kMGT]?B)', s or '0B');
    if not m: return 0
    return float(m.group(1)) * {'B':1e-9,'kB':1e-6,'MB':1e-3,'GB':1,'TB':1000}[m.group(2)]
anon = [v for v in vols if v['Name'] in dangling and re.fullmatch(r'[0-9a-f]{64}', v['Name'])]
named = [v for v in vols if v['Name'] in dangling and v not in anon]
print(f"anonymous dangling: {len(anon)}, total {sum(gb(v['Size']) for v in anon):.1f} GB")
print(f"named dangling (KEPT): {len(named)}, total {sum(gb(v['Size']) for v in named):.1f} GB")
for v in sorted(named, key=lambda v: -gb(v['Size'])): print('  named', v['Name'], v['Size'])
print("top anonymous by size:")
for v in sorted(anon, key=lambda v: -gb(v['Size']))[:12]:
    ins = json.loads(subprocess.run(['docker','volume','inspect',v['Name']],capture_output=True,text=True).stdout)[0]
    lbl = ins.get('Labels') or {}
    print('  ', v['Name'][:12], v['Size'], ins.get('CreatedAt','')[:10], ','.join(f'{k}={lbl[k]}' for k in list(lbl)[:2]))
PY
