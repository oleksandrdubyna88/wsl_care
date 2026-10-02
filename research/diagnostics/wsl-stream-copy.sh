#!/usr/bin/env bash
# Runs in Git Bash. Copies the WSL selection (already listed in the WSL manifest) to V: through a tar
# stream: WSL reads natively, Windows writes to V:. Then hashes the sources in WSL and verifies the copy.
set -euo pipefail
export MSYS_NO_PATHCONV=1
DEST=/v/connectOtherAis/AI_history/wsl-ubuntu
MAN=$DEST/_manifest
WMAN=/mnt/v/connectOtherAis/AI_history/wsl-ubuntu/_manifest

# keep only the files that still exist in WSL (its own cleanups run concurrently)
wsl.exe -d Ubuntu -e bash -c "cd ~ && while read -r p; do [ -f \"\$p\" ] && printf '%s\n' \"\$p\"; done < $WMAN/files.lst" > "$MAN/files.present"
grep -F -x -f "$MAN/files.present" -v "$MAN/files.lst" > "$MAN/vanished.txt" || true
awk -F'\t' 'NR==FNR{k[$0]=1; next} ($5 in k)' "$MAN/files.present" "$MAN/files.tsv" > "$MAN/files.tsv.new"
mv "$MAN/files.tsv.new" "$MAN/files.tsv"; mv "$MAN/files.present" "$MAN/files.lst"
echo "present: $(wc -l < "$MAN/files.lst")  vanished since listing: $(wc -l < "$MAN/vanished.txt")"

wsl.exe -d Ubuntu -e bash -c "cd ~ && tar -cf - -T $WMAN/files.lst" | tar -xf - -C "$DEST"
echo "tar stream done"
wsl.exe -d Ubuntu -e bash -c "cd ~ && xargs -d '\n' -a $WMAN/files.lst sha256sum" > "$MAN/sha256.txt"
echo "hashed: $(wc -l < "$MAN/sha256.txt")"
(cd "$DEST" && sha256sum --quiet -c "$MAN/sha256.txt") && echo "VERIFY OK: $(wc -l < "$MAN/sha256.txt") files match"
