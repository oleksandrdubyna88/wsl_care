#!/bin/bash
# Age distribution of AI-agent session files on the Linux side (read-only).
for spec in "claude:$HOME/.claude/projects:*.jsonl" "codex:$HOME/.codex/sessions:*.jsonl"; do
  n=${spec%%:*}; rest=${spec#*:}; p=${rest%%:*}; g=${rest#*:}
  echo "== $n (wsl)"; find "$p" -type f -name "$g" -printf '%TY-%Tm %s\n' 2>/dev/null | awk '{c[$1]++; s[$1]+=$2; tc++; ts+=$2} END {for (k in c) printf "  %s %6d files %8.2f GB\n", k, c[k], s[k]/1e9; printf "  total %d files %.2f GB\n", tc, ts/1e9}' | sort
  for d in 7 30 60; do find "$p" -type f -name "$g" -mtime +$d -printf '%s\n' 2>/dev/null | awk -v d=$d '{c++; s+=$1} END {printf "  older than %2d d: %6d files %8.2f GB\n", d, c, s/1e9}'; done
done
echo "== claude (wsl) subdirs of a session (jsonl + same-name folder?)"; find ~/.claude/projects -mindepth 2 -maxdepth 2 -type d | head -3; find ~/.claude/projects -mindepth 2 -maxdepth 2 -type d | wc -l
