#!/usr/bin/env bash
# The completeness check a daemon release must pass before it is published (plan §15e #2; E4.S2): a folder of release
# assets holds, for EVERY RID a daemon release ships, its archive and that archive's .sha256 — and nothing else — and
# each .sha256 is in sha256sum's format, names its own archive, and matches the archive's bytes.
#
#   verify-release-assets.sh <version> <dir> [<rid>...]
#
# release.yml runs it twice: over the matrix's artifacts BEFORE anything is uploaded (an incomplete set never reaches
# the draft), and over the assets downloaded back FROM the draft (what will be public is what was checked). The RIDs
# come from lib/daemon-assets.sh, not from the matrix, so a matrix that silently shrank fails here. Named RIDs narrow
# the set to theirs — ci-daemon.yml's legs each pack ONE RID on every pull request and check exactly that pair; every
# other file in the folder is still refused, and a RID no release ships is a usage error.
#
# Every problem is listed, then exit 1; exit 0 only when the set is exactly right. Exit 2 on a bad argument.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/daemon-assets.sh
. "$here/lib/daemon-assets.sh"

[ "$#" -ge 2 ] || { echo "usage: verify-release-assets.sh <version> <dir> [<rid>...]" >&2; exit 2; }
version="$1"
dir="$2"
shift 2
rids="$DAEMON_RIDS"
if [ "$#" -gt 0 ]; then
  for rid in "$@"; do
    daemon_archive_name "$version" "$rid" > /dev/null || { echo "verify-release-assets.sh: not a RID a daemon release ships: $rid (ships: $DAEMON_RIDS)" >&2; exit 2; }
  done
  rids="$*"
fi
[[ "$version" =~ $DAEMON_VERSION_PATTERN ]] || { echo "verify-release-assets.sh: not a release version: $version" >&2; exit 2; }
[ -d "$dir" ] || { echo "verify-release-assets.sh: no folder $dir" >&2; exit 2; }

problems=0
problem() {
  echo "::error::$*"
  problems=$((problems + 1))
}

expected=""
for rid in $rids; do
  archive="$(daemon_archive_name "$version" "$rid")"
  expected="$expected $archive $archive.sha256"
  if [ ! -f "$dir/$archive" ]; then
    problem "$rid: the archive $archive is missing"
  fi
  if [ ! -f "$dir/$archive.sha256" ]; then
    problem "$rid: $archive.sha256 is missing"
    continue
  fi
  line="$(head -n 1 "$dir/$archive.sha256")"
  hash="${line%%  *}"
  name="${line#*  }"
  if ! [[ "$hash" =~ ^[0-9a-f]{64}$ ]] || [ "$name" != "$archive" ]; then
    problem "$rid: $archive.sha256 is not '<sha-256>  $archive'"
    continue
  fi
  if [ -f "$dir/$archive" ]; then
    actual="$(sha256sum "$dir/$archive" | cut -d' ' -f1)"
    [ "$actual" = "$hash" ] || problem "$rid: $archive does not match its .sha256 (expected $hash, got $actual)"
  fi
done

for present in "$dir"/*; do
  [ -e "$present" ] || continue
  base="${present##*/}"
  case " $expected " in
    *" $base "*) ;;
    *) problem "an asset no daemon release ships: $base" ;;
  esac
done

if [ "$problems" -ne 0 ]; then
  echo "verify-release-assets.sh: $problems problem(s) in $dir — this release is NOT complete" >&2
  exit 1
fi
echo "verify-release-assets.sh: every RID asked for ($rids) has its archive and a matching .sha256, nothing else"
