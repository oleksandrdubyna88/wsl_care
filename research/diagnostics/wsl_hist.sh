#!/bin/bash
S() { echo; echo "===== $1 ====="; }
S "journal files"; ls -la --time-style=long-iso /var/log/journal/*/ | head -40; ls /var/log/journal/*/ | wc -l
S "machine-id"; cat /etc/machine-id; ls /var/log/journal/
S "journalctl all files (merge, any machine id)"; journalctl --no-pager -q -D /var/log/journal --merge -o short-iso 2>&1 | head -2; journalctl --no-pager -q --file '/var/log/journal/*/*' -o short-iso 2>&1 | head -2
S "journal verify"; journalctl --verify -q 2>&1 | tail -5
S "syslog/kern.log span"; ls -la --time-style=long-iso /var/log/syslog* /var/log/kern.log* /var/log/dmesg* 2>/dev/null; head -1 /var/log/syslog 2>/dev/null; for f in /var/log/syslog.*.gz; do zcat "$f" 2>/dev/null | head -1; done
S "OOM in rotated syslogs"; zgrep -hE 'Out of memory|Killed process|invoked oom-killer|hung_task|blocked for more than' /var/log/syslog* /var/log/kern.log* 2>/dev/null | tail -40
S "apt history span"; ls -la --time-style=long-iso /var/log/apt/ 2>/dev/null
S "vscode-server logs"; ls -d ~/.vscode-server/data/logs/* 2>/dev/null | head -3; ls -d ~/.vscode-server/data/logs/* 2>/dev/null | wc -l; ls -d ~/.vscode-server/data/logs/* 2>/dev/null | tail -3
S "vscode-server extension hosts / servers count"; ls ~/.vscode-server/bin 2>/dev/null | wc -l; du -sh ~/.vscode-server/bin ~/.vscode-server/extensions ~/.vscode-server/data 2>/dev/null
S "git repos sizes"; du -sh ~/git/* 2>/dev/null | sort -rh | head -20
S "worktrees and node_modules count"; find ~/git ~/coai-* -maxdepth 4 -type d -name node_modules -prune 2>/dev/null | wc -l; find ~/git ~/coai-* -maxdepth 5 -type d \( -name bin -o -name obj \) -prune 2>/dev/null | xargs -r du -sc 2>/dev/null | tail -1
S "dew_flow logs folders"; find ~ -maxdepth 5 -type d -name logs -path '*dew_flow*' 2>/dev/null | head; find ~ -maxdepth 6 -type d -name logs -path '*dew_flow*' 2>/dev/null | xargs -r du -sh 2>/dev/null | head
S "claude/codex logs"; du -sh ~/.claude/projects ~/.claude/debug ~/.claude/shell-snapshots ~/.claude/todos 2>/dev/null; ls ~/.claude/debug 2>/dev/null | wc -l
S "autostart / cron of user"; crontab -l 2>/dev/null; ls ~/.config/systemd/user 2>/dev/null; cat ~/.bashrc | grep -nE 'nvm|sdkman|conda|ssh-agent|eval|source' | head -20
S "lingering"; loginctl show-user 1000 2>/dev/null | egrep 'Linger|State'; ls /var/lib/systemd/linger 2>/dev/null
