# shellcheck shell=sh
# The ONE x.y.z comparison of the release machinery (E5 code round #6): release-extension-guard.sh asks whether the
# verified daemon is at or above the extension's minimum, and POST_DEPLOY.md item 6 asks whether the newest published
# extension tag is the highest version the Marketplace serves — both through these functions, so the two can never
# disagree about whether 0.10.0 is above 0.9.0. POSIX sh: post-deploy-check runs item 6 under sh, the guard under bash.
# Numbers are compared as numbers, field by field; a version is exactly <digits>.<digits>.<digits> (no pre-release:
# neither an extension nor a daemon release carries one, and a line that is not x.y.z sorts as if it were 0.0.0).

# version_sort: the x.y.z lines on stdin, lowest first.
version_sort() {
  sort -t. -k1,1n -k2,2n -k3,3n
}

# version_at_least <have> <need>: succeeds when <have> >= <need>.
version_at_least() {
  [ "$(printf '%s\n%s\n' "$2" "$1" | version_sort | head -n 1)" = "$2" ]
}

# highest_version: the highest x.y.z line on stdin; nothing when stdin is empty.
highest_version() {
  version_sort | tail -n 1
}

# is_top_version <version>: succeeds when <version> is one of the lines on stdin AND no line sorts above it — the served
# list CONTAINS the tag's version and ranks it first, whatever order the list came in.
is_top_version() {
  set -- "$1" "$(cat)"
  printf '%s\n' "$2" | grep -qxF -- "$1" && [ "$(printf '%s\n' "$2" | highest_version)" = "$1" ]
}
