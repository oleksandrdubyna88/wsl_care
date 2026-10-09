#!/usr/bin/env bash
# Waits until the Marketplace's gallery serves <extension-id> <version>, as `vsce show --json` reads it — release-extension.yml's
# publish-marketplace job, after `vsce publish`.
#
#   wait-marketplace-served.sh <extension-id> <version> <attempts> <interval-seconds> <attempt-timeout-seconds>
#
# Measured 2026-10-09 (extension 0.2.0): `vsce publish` succeeded at 15:58:44Z, the gallery's own extensionquery served 0.2.0 at
# 16:12Z, and `vsce show` still listed only 0.1.0 when the job's 20-minute timeout cancelled it at 16:23:28Z — the propagation to
# what `vsce show` reads can exceed 20 minutes. A cancelled job keeps no log of what it saw, so EVERY attempt prints its own
# line (the vsce exit, the versions it listed), and the caller's job limit stays above this wait's budget: the error below,
# with its log, is what ends a slow propagation. Each `vsce show` is bounded (coai plan round 2026-10-09): a hung one is killed
# at <attempt-timeout-seconds> (exit 124, as `timeout` reports it) and counted as a failed attempt, so the worst case is
# attempts x (interval + attempt timeout), which the job limit stays above.
#
# VSCE_BIN overrides the vsce command (a test's stand-in); the job leaves it unset. Exit 0 served, 1 not served after the
# attempts, 2 a bad argument.
set -euo pipefail

[ "$#" -eq 5 ] || { echo "usage: wait-marketplace-served.sh <extension-id> <version> <attempts> <interval-seconds> <attempt-timeout-seconds>" >&2; exit 2; }
id="$1"
version="$2"
attempts="$3"
interval="$4"
per_attempt="$5"
case "$attempts$interval$per_attempt" in
  *[!0-9]*) echo "wait-marketplace-served.sh: attempts, interval and attempt timeout must be whole numbers" >&2; exit 2 ;;
esac
vsce="${VSCE_BIN:-node src_vs_code/node_modules/@vscode/vsce/vsce}"

seen="nothing yet"
for attempt in $(seq 1 "$attempts"); do
  code=0
  # shellcheck disable=SC2086 # the vsce command is a program and its script, split on purpose
  timeout -k 5 "$per_attempt" $vsce show "$id" --json > shown.json 2> shown.err || code=$?
  # The whole list decides; the newest five are what the log shows. An answer that is not JSON with versions lists none
  # (vsce 4.0.0 prints `undefined` and exits 0 for an extension it does not know, observed 2026-10-04).
  answer="$(node -e 'let v=[]; try { v=(JSON.parse(require("fs").readFileSync("shown.json","utf8")).versions||[]).map(x=>x.version); } catch { v=[]; } process.stdout.write((v.includes(process.argv[1]) ? "served" : "not") + "|" + v.slice(0,5).join(", "))' "$version" 2> /dev/null || echo "not|")"
  versions="${answer#*|}"
  if [ "$code" = 0 ] && [ "${answer%%|*}" = served ]; then
    echo "attempt $attempt/$attempts: the Marketplace serves $id $version"
    exit 0
  fi
  seen="versions ${versions:-none listed} (vsce show exited $code)"
  echo "attempt $attempt/$attempts: vsce show exited $code, versions ${versions:-none listed}"
  [ "$attempt" -eq "$attempts" ] || sleep "$interval"
done
echo "::error::the Marketplace did not serve $id $version after $attempts attempts ($((attempts * interval / 60)) min) — last seen: $seen. vsce publish already ran: re-run this job (it skips the publish and waits again)"
exit 1
