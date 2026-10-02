#!/bin/bash
S() { echo; echo "===== $1 ====="; }
K() { for f in $(ls -tr /var/log/kern.log* 2>/dev/null); do case "$f" in *.gz) zcat "$f";; *) cat "$f";; esac; done; }
Y() { for f in $(ls -tr /var/log/syslog* 2>/dev/null); do case "$f" in *.gz) zcat "$f";; *) cat "$f";; esac; done; }
K > /tmp/k.log; Y > /tmp/y.log
S "page alloc failure context (Mem-Info)"; grep -a -n 'page allocation failure' /tmp/k.log | head -2
L=$(grep -a -n 'page allocation failure' /tmp/k.log | head -1 | cut -d: -f1); sed -n "$((L)),$((L+75))p" /tmp/k.log | grep -a -E 'Mem-Info|active_anon|inactive_anon|active_file|inactive_file|free:|slab_reclaimable|Normal free|Node 0 Normal:|Node 0 DMA32:|pagecache pages|Free swap|Total swap|pages RAM|shmem|CPU:|Comm:|Call Trace' | cut -c33-300
S "kernel messages by kind (normalized)"; grep -a ' kernel: ' /tmp/k.log | sed -E 's/^[^ ]+ [^ ]+ kernel: //; s/\[ *[0-9.]+\] //; s/[0-9a-f]{6,}/H/g; s/[0-9]+/N/g' | cut -c1-80 | sort | uniq -c | sort -rn | head -25
S "kernel lines per day"; cut -c1-10 /tmp/k.log | sort | uniq -c | tail -35
S "docker lines (normalized)"; grep -a -iE 'docker|containerd' /tmp/y.log | sed -E 's/^[^ ]+ [^ ]+ //; s/[0-9a-f]{12,}/H/g; s/[0-9]+/N/g' | cut -c1-110 | sort | uniq -c | sort -rn | head -12
S "clock jump emitters"; grep -a -E 'Time jumped backwards|Clock change detected|Time has been changed' /tmp/y.log | awk '{print $3}' | sed -E 's/\[[0-9]+\]//' | sort | uniq -c
S "wsl-pro-service sample"; grep -a 'wsl-pro-service' /tmp/y.log | sed -E 's/^[^ ]+ [^ ]+ [^ ]+ //; s/[0-9]+/N/g' | cut -c1-140 | sort | uniq -c | sort -rn | head -5
S "eth / network flaps per day"; grep -a -E 'link becomes ready|NIC Link is|carrier|hv_netvsc' /tmp/k.log | cut -c1-10 | sort | uniq -c | tail -15
S "ext4 / disk errors"; grep -a -iE 'EXT4-fs (error|warning)|I/O error|blk_update_request|Buffer I/O' /tmp/k.log | sed -E 's/^[^ ]+ [^ ]+ //' | cut -c1-140 | sort | uniq -c | sort -rn | head -10
S "swap usage events"; grep -a -iE 'swap' /tmp/k.log | grep -a -v 'Adding' | cut -c1-160 | tail -5
rm -f /tmp/k.log /tmp/y.log
