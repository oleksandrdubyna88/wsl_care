#!/usr/bin/env bash
# The guard every daemon release passes before any leg builds (E4.S2): the tag is exactly `daemon-v<version>` with a
# version install.sh accepts, src_daemon/version.txt at that tag says the same version (release-please's `simple`
# strategy keeps that file — a tag that disagrees would ship a binary whose --version is another release), and — when a
# main ref is given — the tagged commit is on main (release-please cuts tags only from merged release pull requests;
# this holds even before the tag ruleset in .github/rulesets/ is applied).
#
#   release-guard.sh <tag> [<main-ref>]
#
# Run from the repository root of the tag's checkout. On success prints `version=<version>` — appended to
# $GITHUB_OUTPUT too when that is set — and exits 0; any refusal exits 1 naming the reason. The tag reaches this
# script as an argument from the environment, never pasted into shell source (it is text somebody chose).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/daemon-assets.sh
. "$here/lib/daemon-assets.sh"

refuse() {
  echo "::error::release guard: $*"
  exit 1
}

[ "$#" -ge 1 ] && [ "$#" -le 2 ] || { echo "usage: release-guard.sh <tag> [<main-ref>]" >&2; exit 2; }
tag="$1"

case "$tag" in
  daemon-v*) version="${tag#daemon-v}" ;;
  *) refuse "the tag '$tag' is not daemon-v<version> — this workflow releases the daemon only" ;;
esac
[[ "$version" =~ $DAEMON_VERSION_PATTERN ]] || refuse "the tag '$tag' does not carry a release version (x.y.z[-pre])"

[ -f src_daemon/version.txt ] || refuse "src_daemon/version.txt is missing at this checkout"
recorded="$(tr -d '[:space:]' < src_daemon/version.txt)"
[ "$recorded" = "$version" ] || refuse "the tag says $version but src_daemon/version.txt at the tag says '$recorded' — tag the commit release-please bumped, never move a tag"

if [ "$#" -eq 2 ]; then
  git merge-base --is-ancestor HEAD "$2" 2> /dev/null || refuse "the tagged commit is not on $2 — a release is cut from main only"
fi

echo "version=$version"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  echo "version=$version" >> "$GITHUB_OUTPUT"
fi
