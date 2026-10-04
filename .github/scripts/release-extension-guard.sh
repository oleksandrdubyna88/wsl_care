#!/usr/bin/env bash
# The guard every extension release passes before anything is built (E5.S3, plan §15g M4/M5). It refuses unless:
#
#   1. the tag is exactly `extension-v<x.y.z>`;
#   2. src_vs_code/package.json at that tag carries the same version (release-please's `node` strategy bumps it — a tag
#      that disagrees would publish another version to the Marketplace than the tag says);
#   3. the publisher is a real Marketplace id, not the placeholder (the owner creates it at the E5 live gate, step 1);
#   4. when a main ref is given, the tagged commit is on main;
#   5. THE MECHANICAL LIVE GATE (M4): the minimum daemon this extension renders — read from the checked-in artefact
#      src_vs_code/min-daemon.json (`{ "minDaemonForRender": "x.y.z" }`) with a JSON parser, never from TypeScript with a
#      line pattern (E5 code round #2/#5; scripts/bundle.mjs emits the same value from `MIN_DAEMON_FOR_RENDER` at bundle
#      time, and the extension's tests and check-vsix hold the checked-in copy equal to it) — is a PUBLISHED, non-draft
#      GitHub release `daemon-v<MIN>` (asked through `gh api` with the job's read-only token), AND POST_DEPLOY.md's
#      `Last verified:` line names a date and a verified daemon at or above that minimum (`… · daemon <x.y.z> …`, compared
#      by lib/versions.sh, the functions POST_DEPLOY item 6 ranks versions with). An extension whose *Install daemon*
#      types `--version <MIN>` must never ship before that daemon is out and was seen working.
#
#   release-extension-guard.sh <tag> [<main-ref>]
#
# Run from the repository root of the tag's checkout, with GH_REPO (owner/name) and GH_TOKEN set for `gh api`. On
# success prints `version=…`, `publisher=…` and `min_daemon=…` — appended to $GITHUB_OUTPUT too when that is set; each is
# a declared output of release-extension.yml's guard job, and the build checks its .vsix against min_daemon — and exits 0;
# any refusal exits 1 naming the reason. The tag reaches this script as an argument from the environment, never pasted
# into shell source.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/versions.sh
. "$here/lib/versions.sh"

readonly EXTENSION_VERSION_PATTERN='^[0-9]+\.[0-9]+\.[0-9]+$'
readonly PUBLISHER_PATTERN='^[a-z0-9][a-z0-9-]*$'
readonly PUBLISHER_PLACEHOLDER='publisher-tbd'
readonly MANIFEST='src_vs_code/package.json'
readonly MIN_DAEMON_FILE='src_vs_code/min-daemon.json'
readonly STAMP_FILE='POST_DEPLOY.md'

refuse() {
  echo "::error::extension release guard: $*"
  exit 1
}

[ "$#" -ge 1 ] && [ "$#" -le 2 ] || { echo "usage: release-extension-guard.sh <tag> [<main-ref>]" >&2; exit 2; }
tag="$1"

case "$tag" in
  extension-v*) version="${tag#extension-v}" ;;
  *) refuse "the tag '$tag' is not extension-v<version> — this workflow releases the extension only" ;;
esac
[[ "$version" =~ $EXTENSION_VERSION_PATTERN ]] || refuse "the tag '$tag' does not carry an x.y.z version"

# One top-level `"key": "value"` of package.json (npm writes it two-space indented, one key per line); exactly one.
manifest_field() {
  local found
  found="$(sed -n "s/^  \"$1\": \"\\([^\"]*\\)\",\\{0,1\\}\$/\\1/p" "$MANIFEST")"
  [ -n "$found" ] && [ "$(printf '%s\n' "$found" | wc -l)" -eq 1 ] || refuse "$MANIFEST carries no single top-level \"$1\""
  printf '%s\n' "$found"
}

[ -f "$MANIFEST" ] || refuse "$MANIFEST is missing at this checkout"
recorded="$(manifest_field version)"
[ "$recorded" = "$version" ] || refuse "the tag says $version but $MANIFEST at the tag says '$recorded' — tag the commit release-please bumped, never move a tag"

publisher="$(manifest_field publisher)"
[ "$publisher" != "$PUBLISHER_PLACEHOLDER" ] || refuse "$MANIFEST still carries the placeholder publisher '$PUBLISHER_PLACEHOLDER' — the owner creates the Marketplace publisher first (E5 live gate, step 1)"
[[ "$publisher" =~ $PUBLISHER_PATTERN ]] || refuse "the publisher '$publisher' is not a Marketplace id (lower-case letters, digits, dashes)"

if [ "$#" -eq 2 ]; then
  git merge-base --is-ancestor HEAD "$2" 2> /dev/null || refuse "the tagged commit is not on $2 — a release is cut from main only"
fi

# The minimum daemon, from the JSON artefact, read by a JSON parser (python3, on every Ubuntu runner): neither a
# reformatted TypeScript source nor a second matching line can change what the guard believes.
[ -f "$MIN_DAEMON_FILE" ] || refuse "$MIN_DAEMON_FILE is missing at this checkout — the minimum daemon this extension renders, emitted by scripts/bundle.mjs from MIN_DAEMON_FOR_RENDER and checked in beside it"
min="$(python3 -c '
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as f:
        value = json.load(f).get("minDaemonForRender")
except (ValueError, AttributeError):
    value = None
print(value if isinstance(value, str) else "")
' "$MIN_DAEMON_FILE")"
[[ "$min" =~ $EXTENSION_VERSION_PATTERN ]] || refuse "$MIN_DAEMON_FILE carries no minDaemonForRender \"x.y.z\" — restore it from the extension's MIN_DAEMON_FOR_RENDER (npm test holds the two equal)"

# The minimum daemon is a PUBLISHED release. GitHub answers a draft's tag with 404 to a read-only token, and the jq
# filter makes a published one print `false<TAB>daemon-v<MIN>` — anything else is not a published release.
daemon_tag="daemon-v$min"
answer="$(gh api "repos/${GH_REPO:?GH_REPO is required}/releases/tags/$daemon_tag" --jq '[(.draft | tostring), .tag_name] | @tsv' 2> /dev/null)" \
  || refuse "the minimum daemon $daemon_tag is not a published release of $GH_REPO — publish it first (the E4 live gate), then tag the extension"
[ "$answer" = "false	$daemon_tag" ] || refuse "the minimum daemon $daemon_tag is not a published, non-draft release (GitHub answered '$answer')"

# …and was seen working: POST_DEPLOY.md's stamp names a date and a daemon at or above the minimum.
[ -f "$STAMP_FILE" ] || refuse "$STAMP_FILE is missing at this checkout"
stamp="$(sed -n 's/^Last verified: //p' "$STAMP_FILE" | head -n 1)"
[[ "$stamp" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}\  ]] || refuse "$STAMP_FILE's 'Last verified:' line names no date — the minimum daemon $min was never verified live (run POST_DEPLOY.md and stamp it)"
verified="$(printf '%s\n' "$stamp" | sed -n 's/.*daemon \([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/p')"
[ -n "$verified" ] || refuse "$STAMP_FILE's 'Last verified:' line names no 'daemon <x.y.z>' — stamp the verified daemon version"
version_at_least "$verified" "$min" || refuse "$STAMP_FILE last verified daemon $verified, older than the minimum $min this extension needs"

for line in "version=$version" "publisher=$publisher" "min_daemon=$min"; do
  echo "$line"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    echo "$line" >> "$GITHUB_OUTPUT"
  fi
done
