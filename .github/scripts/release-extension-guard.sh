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
#      types `--version <MIN>` must never ship before that daemon is out and was seen working. Since E6.S2 (plan §15j
#      M5) the artefact holds TWO minima — `minDaemonForRender` and `minDaemonForActions` (the daemon the cleanups need,
#      the one *Install daemon* types) — and BOTH must be published releases, and the stamp at or above both.
#      THE STAMP IS READ FROM MAIN'S TIP when a main ref is given (`git show <main-ref>:POST_DEPLOY.md`), never from the
#      tag's tree: a stamp is a post-release fact, recorded after the daemon was installed and seen working, so it lands on
#      main AFTER the commit the tag points at — read from the tag it could never be there (extension-v0.1.0's guard refused
#      "names no date" on a re-run after the stamp had merged, 2026-10-08). The job's full-history checkout fetches main
#      afresh on every run, so a stamp merged after the tag counts on "Re-run failed jobs". Everything else — package.json,
#      min-daemon.json and its pins, the root module — is read at the tag. Without a main ref (a local run) the stamp is read
#      from the checkout. Every stamp refusal names which of the two it read;
#   6. THE FIRST PUBLIC EXTENSION STAYS ROOT-FREE (plan §15j B3, keyed on TAGS by §15k #7, tightened by the E6.S2 review
#      S1): a checkout that carries the root module (src_vs_code/src/root/rootCall.ts) is refused unless ALL of: the release
#      is above `extension-v0.1.0`; that tag exists and its OWN tree carries no root module (a refused 0.1.0 that carried it
#      stays tagged — the ruleset blocks deleting a tag — and must not open the door); and `extension-v0.1.0` is a PUBLISHED,
#      non-draft GitHub release (the root-free one actually shipped). The answer is also an output, `root_allowed`, and the build hands it to
#      check-vsix, which refuses the BUNDLE when it carries the root module's marker and root_allowed is false — the
#      source here, the artefact there.
#
#   release-extension-guard.sh <tag> [<main-ref>]
#
# Run from the repository root of the tag's checkout (the whole history: the tags are read, and main's tip, where the stamp
# is read), with GH_REPO (owner/name) and
# GH_TOKEN set for `gh api`. On success prints `version=…`, `publisher=…`, `min_daemon=…`, `min_daemon_actions=…` and
# `root_allowed=…` — appended to $GITHUB_OUTPUT too when that is set; each is a declared output of release-extension.yml's
# guard job, and the build checks its .vsix against them — and exits 0; any refusal exits 1 naming the reason. The tag
# reaches this script as an argument from the environment, never pasted into shell source.
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
readonly ROOT_MODULE='src_vs_code/src/root/rootCall.ts'
readonly FIRST_PUBLIC='0.1.0'

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

# published_release <tag>: succeeds when <tag> is a PUBLISHED, non-draft GitHub release. GitHub answers a draft's tag with 404
# to a read-only token, and the jq filter makes a published one print `false<TAB><tag>` — anything else is not published.
published_release() {
  local answer
  answer="$(gh api "repos/${GH_REPO:?GH_REPO is required}/releases/tags/$1" --jq '[(.draft | tostring), .tag_name] | @tsv' 2> /dev/null)" || return 1
  [ "$answer" = "false	$1" ]
}

# The first public extension stays root-free (plan §15j B3, keyed on TAGS — §15k #7 — and the E6.S2 review S1). The tags
# and trees come from the guard job's full-history checkout; GitHub is asked last, and only when everything local allows.
first_public="extension-v$FIRST_PUBLIC"
root_allowed=false
if [ "$version" = "$FIRST_PUBLIC" ] || ! version_at_least "$version" "$FIRST_PUBLIC"; then
  root_refusal="extension-v$version is the first public extension or earlier — it must stay root-free (plan §15j B3); the root boundary ships above $first_public"
elif ! git rev-parse -q --verify "refs/tags/$first_public^{commit}" > /dev/null 2>&1; then
  root_refusal="no $first_public tag exists yet — the first public extension is tagged root-free from E5's merge first (plan §15j B3, §15k #7)"
elif git cat-file -e "refs/tags/$first_public:$ROOT_MODULE" 2> /dev/null; then
  root_refusal="$first_public was tagged from a tree that carries the root module, so the first public extension never shipped root-free (plan §15j B3, E6.S2 review S1)"
elif ! published_release "$first_public"; then
  root_refusal="$first_public is not a published, non-draft GitHub release yet — a root-capable extension waits until the root-free one shipped (plan §15j B3, E6.S2 review S1); re-run this job once it is public"
else
  root_allowed=true
fi
if [ -f "$ROOT_MODULE" ] && [ "$root_allowed" != true ]; then
  refuse "this checkout carries the root module ($ROOT_MODULE), but $root_refusal"
fi

# The minima, from the JSON artefact, read by a JSON parser (python3, on every Ubuntu runner): neither a reformatted
# TypeScript source nor a second matching line can change what the guard believes.
[ -f "$MIN_DAEMON_FILE" ] || refuse "$MIN_DAEMON_FILE is missing at this checkout — the minimum daemon this extension renders, emitted by scripts/bundle.mjs from MIN_DAEMON_FOR_RENDER and checked in beside it"

# minimum_of <key>: the x.y.z string under <key> in the artefact, or nothing.
minimum_of() {
  python3 -c '
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as f:
        value = json.load(f).get(sys.argv[2])
except (ValueError, AttributeError):
    value = None
print(value if isinstance(value, str) else "")
' "$MIN_DAEMON_FILE" "$1"
}

min="$(minimum_of minDaemonForRender)"
[[ "$min" =~ $EXTENSION_VERSION_PATTERN ]] || refuse "$MIN_DAEMON_FILE carries no minDaemonForRender \"x.y.z\" — restore it from the extension's MIN_DAEMON_FOR_RENDER (npm test holds the two equal)"
min_actions="$(minimum_of minDaemonForActions)"
[[ "$min_actions" =~ $EXTENSION_VERSION_PATTERN ]] || refuse "$MIN_DAEMON_FILE carries no minDaemonForActions \"x.y.z\" — restore it from the extension's MIN_DAEMON_FOR_ACTIONS (npm test holds the two equal)"
# A daemon the extension acts with must also render (coai E6.S2 code round #0).
version_at_least "$min_actions" "$min" || refuse "$MIN_DAEMON_FILE: minDaemonForActions $min_actions is below minDaemonForRender $min — a daemon the extension acts with must be one it renders"
# The release Install daemon types (#37, 2026-10-06: daemon 0.1.0's act unit is defective, so a new install gets 0.1.2) is a
# value of its own, at or above both minima: a fresh install must render AND act.
install="$(minimum_of installDaemon)"
[[ "$install" =~ $EXTENSION_VERSION_PATTERN ]] || refuse "$MIN_DAEMON_FILE carries no installDaemon \"x.y.z\" — restore it from the extension's INSTALL_DAEMON (npm test holds the two equal)"
version_at_least "$install" "$min" || refuse "$MIN_DAEMON_FILE: installDaemon $install is below minDaemonForRender $min — Install daemon would install a daemon this extension refuses"
version_at_least "$install" "$min_actions" || refuse "$MIN_DAEMON_FILE: installDaemon $install is below minDaemonForActions $min_actions — Install daemon would install a daemon this extension cannot act with"

# Each minimum is a PUBLISHED release. GitHub answers a draft's tag with 404 to a read-only token, and the jq filter makes a
# published one print `false<TAB>daemon-v<MIN>` — anything else is not a published release.
require_published() {
  local daemon_tag="daemon-v$1" answer
  answer="$(gh api "repos/${GH_REPO:?GH_REPO is required}/releases/tags/$daemon_tag" --jq '[(.draft | tostring), .tag_name] | @tsv' 2> /dev/null)" \
    || refuse "the minimum daemon $daemon_tag is not a published release of $GH_REPO — publish it first (the E4 live gate), then tag the extension"
  [ "$answer" = "false	$daemon_tag" ] || refuse "the minimum daemon $daemon_tag is not a published, non-draft release (GitHub answered '$answer')"
}
require_published "$min"
if [ "$min_actions" != "$min" ]; then
  require_published "$min_actions"
fi
if [ "$install" != "$min" ] && [ "$install" != "$min_actions" ]; then
  require_published "$install"
fi

# …and was seen working: POST_DEPLOY.md's stamp names a date and a daemon at or above both minima and the install pin. The
# stamp is a post-release fact, so it is read from MAIN'S TIP when a main ref is given — the minima and the pin above stay
# the tag's (see the header, item 5).
if [ "$#" -eq 2 ]; then
  stamp_source="$STAMP_FILE on $2"
  git cat-file -e "$2:$STAMP_FILE" 2> /dev/null || refuse "$STAMP_FILE is missing on $2 — the stamp is read from main's tip, not from the tag"
  stamp_text="$(git show "$2:$STAMP_FILE" 2> /dev/null)" || refuse "$STAMP_FILE on $2 could not be read (git show $2:$STAMP_FILE failed)"
  stamp_advice="the stamp is read from $2's tip: merge the stamp, then Re-run FAILED jobs"
else
  stamp_source="$STAMP_FILE at this checkout"
  [ -f "$STAMP_FILE" ] || refuse "$stamp_source is missing"
  stamp_text="$(cat "$STAMP_FILE")"
  stamp_advice="no main ref was given, so the stamp was read from this checkout — pass the main ref to read main's"
fi
stamp="$(printf '%s\n' "$stamp_text" | sed -n 's/^Last verified: //p' | head -n 1)"
[[ "$stamp" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}\  ]] || refuse "the 'Last verified:' line of $stamp_source names no date — the minimum daemon $min was never verified live (run POST_DEPLOY.md and stamp it; $stamp_advice)"
verified="$(printf '%s\n' "$stamp" | sed -n 's/.*daemon \([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/p')"
[ -n "$verified" ] || refuse "the 'Last verified:' line of $stamp_source names no 'daemon <x.y.z>' — stamp the verified daemon version ($stamp_advice)"
version_at_least "$verified" "$min" || refuse "$stamp_source last verified daemon $verified, older than the minimum $min this extension needs ($stamp_advice)"
version_at_least "$verified" "$min_actions" || refuse "$stamp_source last verified daemon $verified, older than the actions minimum $min_actions this extension acts with ($stamp_advice)"
version_at_least "$verified" "$install" || refuse "$stamp_source last verified daemon $verified, older than $install, the release Install daemon types ($stamp_advice)"

for line in "version=$version" "publisher=$publisher" "min_daemon=$min" "min_daemon_actions=$min_actions" "install_daemon=$install" "root_allowed=$root_allowed"; do
  echo "$line"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    echo "$line" >> "$GITHUB_OUTPUT"
  fi
done
