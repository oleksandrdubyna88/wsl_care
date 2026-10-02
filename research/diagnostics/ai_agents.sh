#!/bin/bash
# Read-only inventory of AI agent CLIs and their on-disk state (Ubuntu side).
S() { echo; echo "===== $1 ====="; }
source ~/.nvm/nvm.sh >/dev/null 2>&1
S "binaries on PATH"
for b in claude codex gemini antigravity agy cursor-agent cursor copilot gh aider opencode amp qwen kiro kiro-cli goose crush rovodev acli cline continue windsurf ollama llm q; do
  p=$(command -v "$b" 2>/dev/null) && printf '%-14s %s\n' "$b" "$(readlink -f "$p")"
done
S "npm global packages that look like agents"; npm ls -g --depth=0 2>/dev/null | grep -iE 'claude|codex|gemini|cursor|copilot|opencode|amp|qwen|kiro|aider|cline|goose|crush|antigravity'
S "dot-dirs in home (size, entries)"
for d in ~/.claude ~/.codex ~/.gemini ~/.antigravity ~/.cache/antigravity ~/.config/antigravity ~/.cursor ~/.cursor-server ~/.copilot ~/.config/github-copilot ~/.aider* ~/.local/share/opencode ~/.config/opencode ~/.amp ~/.config/amp ~/.qwen ~/.kiro ~/.rovodev ~/.config/goose ~/.local/share/goose ~/.cline ~/.continue ~/.windsurf-server ~/.local/share/crush; do
  [ -e "$d" ] && printf '%-36s %8s %6s entries\n' "$d" "$(du -sh "$d" 2>/dev/null | cut -f1)" "$(ls -A "$d" 2>/dev/null | wc -l)"
done
S "claude: projects / session files"; ls ~/.claude/projects 2>/dev/null | wc -l; find ~/.claude/projects -name '*.jsonl' 2>/dev/null | wc -l; du -sh ~/.claude/projects ~/.claude/todos ~/.claude/shell-snapshots ~/.claude/file-history ~/.claude/debug ~/.claude/plugins 2>/dev/null
S "claude: oldest/newest session"; find ~/.claude/projects -name '*.jsonl' -printf '%TY-%Tm-%Td\n' 2>/dev/null | sort | sed -n '1p;$p'
S "claude: top projects by size"; du -sh ~/.claude/projects/* 2>/dev/null | sort -rh | head -5
S "codex: sessions"; find ~/.codex/sessions -name '*.jsonl' 2>/dev/null | wc -l; du -sh ~/.codex/* 2>/dev/null | sort -rh | head -6
S "gemini: tmp/history"; du -sh ~/.gemini/* 2>/dev/null | sort -rh | head -6; ls ~/.gemini/tmp 2>/dev/null | wc -l
S "rovodev"; du -sh ~/.rovodev/* 2>/dev/null | sort -rh | head -4
