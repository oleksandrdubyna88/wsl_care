#!/bin/bash
L=$(grep -a -n 'page allocation failure' /var/log/kern.log | tail -1 | cut -d: -f1)
sed -n "$((L-2)),$((L+90))p" /var/log/kern.log | cut -c40-230 | grep -a -vE '^ *(\? )?[a-z_0-9.]+\+0x|RIP|RSP|RAX|RDX|RBP|R1[0-5]|R0[89]|</?TASK>|Code:'
echo "---- around 18:30-18:40 syslog: what was running"
grep -a '2026-10-01T18:3[0-9]' /var/log/syslog | awk '{print $3}' | sed -E 's/\[[0-9]+\]//' | sort | uniq -c | sort -rn | head -10
