#!/usr/bin/env bash
# Archive AI CLI session logs older than a cutoff.
#   archive-ai-sessions.sh plan   <dest> <cutoff-epoch>
#   archive-ai-sessions.sh copy   <dest> <cutoff-epoch>
#   archive-ai-sessions.sh verify <dest>
#   archive-ai-sessions.sh delete <dest>     (only files whose copy AND source match the manifest hash)
#   archive-ai-sessions.sh delete-source <dest>  (same, the archive already verified by the caller)
# Paths are relative to $HOME and preserved under <dest>.
# A "unit" (one session) is archived only when its NEWEST file is older than the cutoff.
set -euo pipefail
MODE=$1; DEST=$2; CUTOFF=${3:-0}
MAN="$DEST/_manifest"
cd "$HOME"

candidates() {
  {
    # Claude Code: projects/<proj>/<uuid>.jsonl + projects/<proj>/<uuid>/** ; never memory/
    [ -d .claude/projects ] && find .claude/projects -mindepth 2 -type f -not -path '*/memory/*' -printf '%T@\t%s\tclaude\t%p\n'
    # Codex: rollout files
    for d in .codex/sessions .codex/archived_sessions; do
      [ -d "$d" ] && find "$d" -type f -printf '%T@\t%s\tcodex\t%p\n'
    done
    # Gemini CLI: chats
    [ -d .gemini/tmp ] && find .gemini/tmp -type f -path '*/chats/*' -printf '%T@\t%s\tgemini\t%p\n'
    # Antigravity (Gemini agent): per-conversation files + cli logs
    for b in .gemini/antigravity-cli .gemini/antigravity; do
      for s in conversations brain annotations presence log; do
        [ -d "$b/$s" ] && find "$b/$s" -type f -printf '%T@\t%s\tantigravity\t%p\n'
      done
    done
  } 2>/dev/null || true
}

# key = the session a file belongs to
keyed() {
  awk -F'\t' 'BEGIN{OFS="\t"} {
    p=$4; n=split(p,a,"/"); k=p
    if ($3=="claude") { x=a[4]; sub(/\.jsonl$/,"",x); k=a[1]"/"a[2]"/"a[3]"/"x }
    else if ($3=="antigravity" && a[3]!="log") { x=a[4]; sub(/\..*$/,"",x); k=a[1]"/"a[2]"/"x }
    print $1,$2,$3,k,p }'
}

select_old() {
  candidates | keyed | awk -F'\t' -v c="$CUTOFF" 'BEGIN{OFS="\t"}
    { r[NR]=$0; if ($1+0>m[$4]+0) m[$4]=$1 }
    END { for (i=1;i<=NR;i++) { split(r[i],f,"\t"); if (m[f[4]]+0 < c) print f[1],f[2],f[3],f[4],f[5] } }'
}

summary() { # stdin: tsv rows
  awk -F'\t' '{ n[$3]++; s[$3]+=$2; u[$3 SUBSEP $4]=1 }
    END { for (k in u) { split(k,z,SUBSEP); un[z[1]]++ }
          for (t in n) printf "  %-12s sessions=%-6d files=%-6d %8.1f MB\n", t, un[t], n[t], s[t]/1048576 }'
}

case "$MODE" in
  plan)
    echo "All candidates:";      candidates | keyed | summary
    echo "Older than cutoff:";   select_old | summary ;;
  copy)
    mkdir -p "$MAN"
    select_old | sort -t$'\t' -k5 > "$MAN/files.tsv"
    cut -f5 "$MAN/files.tsv" > "$MAN/files.lst"
    xargs -d '\n' -a "$MAN/files.lst" cp --parents --preserve=timestamps -t "$DEST" 2>"$MAN/copy-errors.txt" || true
    # a file the tool removed itself between listing and copying drops out of the manifest
    while IFS=$'\t' read -r t s tool k p; do
      if [ -f "$p" ] && [ -f "$DEST/$p" ]; then printf '%s\t%s\t%s\t%s\t%s\n' "$t" "$s" "$tool" "$k" "$p"; fi
    done < "$MAN/files.tsv" > "$MAN/files.tmp" && mv "$MAN/files.tmp" "$MAN/files.tsv"
    cut -f5 "$MAN/files.tsv" > "$MAN/files.lst"
    echo "copy errors (vanished files): $(grep -c . "$MAN/copy-errors.txt" || true)"
    xargs -d '\n' -a "$MAN/files.lst" sha256sum > "$MAN/sha256.txt"
    echo "Copied:"; summary < "$MAN/files.tsv"
    (cd "$DEST" && sha256sum --quiet -c "$MAN/sha256.txt") && echo "VERIFY OK: $(wc -l < "$MAN/sha256.txt") files match" ;;
  verify)
    (cd "$DEST" && sha256sum --quiet -c "$MAN/sha256.txt") && echo "VERIFY OK: $(wc -l < "$MAN/sha256.txt") files match" ;;
  delete|delete-source)
    # delete-source: the archive was verified by the caller from the side that reads <dest> fast
    if [ "$MODE" = delete ]; then
      (cd "$DEST" && sha256sum --quiet -c "$MAN/sha256.txt") || { echo "ARCHIVE MISMATCH - nothing deleted"; exit 1; }
    fi
    # delete a source file only while it still hashes to what was archived; changed or vanished ones are skipped
    { sha256sum -c "$MAN/sha256.txt" 2>/dev/null || true; } > "$MAN/source-check.txt"
    sed -n 's/: OK$//p' "$MAN/source-check.txt" > "$MAN/deletable.lst"
    grep -v ': OK$' "$MAN/source-check.txt" > "$MAN/skipped.txt" || true
    xargs -d '\n' -a "$MAN/deletable.lst" rm -f
    # remove session dirs left empty by this delete (depth >= 4; tool roots and project dirs stay)
    { while read -r p; do d=$(dirname "$p"); echo "$d"; dirname "$d"; done < "$MAN/deletable.lst"; } \
      | sort -u | awk -F/ 'NF>=4 && $0 !~ /\/memory(\/|$)/ { print NF "\t" $0 }' | sort -rn | cut -f2- \
      | while read -r d; do rmdir --ignore-fail-on-non-empty "$d" 2>/dev/null || true; done
    echo "DELETED $(wc -l < "$MAN/deletable.lst") files; skipped (changed or already gone): $(wc -l < "$MAN/skipped.txt")" ;;
esac
