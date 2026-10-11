#!/usr/bin/env bash
# THE REAL CHECK of the bundled Windows daemon (E7.S5a, research/PLAN_bundle_windows_binary.md §3.5 — own Fable review, finding 1,
# Blocking): the build job is the untrusted domain by design (npm ci with install scripts, every test, a downloaded VS Code,
# vsce package -> vscode:prepublish -> esbuild), and any of them could rewrite BOTH bin/wsl-care.exe and its digest after the
# fetch. So the attest job — no npm, no node, a sparse .github/scripts checkout — re-derives the bytes itself before it signs,
# and github-public repeats it before the release goes public:
#
#   verify-bundled-exe.sh <bundled-daemon-version> <package.vsix>
#
#   1. fetch-windows-daemon.sh <version> into a fresh folder — the release download, verify-release-assets.sh, the EXACT
#      attestation of release.yml@refs/tags/daemon-v<version>, the one zip member read by its exact name;
#   2. python's zipfile reads EVERY entry name of the package (own Fable review of the code, finding 1): VS Code's installer
#      writes `<ext>/<name>` on case-insensitive NTFS and the last of two colliding entries wins, and the exe loads DLLs from
#      its own folder first — so the package is refused when any name is unsafe (`.`/`..`/empty segments, `\`, `:`, a leading
#      `/`) or a symlink, when two names are one file case-insensitively, when anything but the exe is under `extension/bin/`,
#      or when any other entry is an `.exe` or a `.dll`; then `extension/bin/wsl-care.exe` must be there exactly once;
#   3. the two are compared byte for byte.
#
# It trusts what `daemon-v<version>` serves AT ITS RUN TIME, attested: replacing a published asset needs `contents: write`
# and is refused by the tag ruleset and the never-replace rule of the release (finding 14).
#
# Exit 0 and one line naming both digests when they are the same; 1 on any difference, a missing member or a refused fetch;
# 2 on usage. Nothing is extracted by an archive-supplied path.
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: verify-bundled-exe.sh <bundled-daemon-version> <package.vsix>" >&2; exit 2; }
version="$1"
vsix="$2"
[ -f "$vsix" ] || { echo "verify-bundled-exe.sh: $vsix is not a file" >&2; exit 2; }

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

refuse() {
  echo "::error::verify-bundled-exe: $*"
  exit 1
}

# native and python_cmd: one copy for every script that reads a zip.
# shellcheck source=lib/windows-tools.sh
. "$here/lib/windows-tools.sh"

bash "$here/fetch-windows-daemon.sh" "$version" "$work/attested" \
  || refuse "the attested daemon-v$version member could not be fetched and verified — nothing is signed"
py="$(python_cmd)" || refuse "no python with zipfile here (python3 or python)"
"$py" -I -B - "$(native "$vsix")" "$(native "$work/attested/bin/wsl-care.exe")" "$(native "$here/lib")" <<'PYTHON' || refuse "$vsix does not carry exactly the attested daemon-v$version member — nothing is signed"
import hashlib, posixpath, stat, sys, zipfile

package, attested_path = sys.argv[1], sys.argv[2]
sys.path.insert(0, sys.argv[3])
from zip_entries import unsafe  # the ONE entry-name rule, shared with fetch-windows-daemon.sh

ENTRY = "extension/bin/wsl-care.exe"
BIN = "extension/bin/"

try:
    archive = zipfile.ZipFile(package)
except Exception as error:
    print(f"not a readable package: {error}", file=sys.stderr)
    sys.exit(1)

def refusal(info, seen):
    name = info.orig_filename
    if unsafe(name) or stat.S_ISLNK(info.external_attr >> 16):
        return f"an unsafe entry: {name!r}"
    folded = posixpath.normpath(name).casefold()
    if folded in seen and name != ENTRY:
        return f"{name!r} is the same file as another entry on a case-insensitive disk"
    seen.add(folded)
    if name != ENTRY and (folded.startswith(BIN) or folded.endswith((".exe", ".dll"))):
        return f"{name!r} is code beside the attested exe — only {ENTRY} may be"
    return ""

seen = set()
for info in archive.infolist():
    why = refusal(info, seen)
    if why:
        print(why, file=sys.stderr)
        sys.exit(1)

named = [info for info in archive.infolist() if info.orig_filename == ENTRY]
if len(named) != 1:
    print(f"the package holds {len(named)} entries named {ENTRY}, not exactly one", file=sys.stderr)
    sys.exit(1)

bundled = archive.read(named[0])
with open(attested_path, "rb") as handle:
    attested = handle.read()
if bundled != attested:
    print(f"{ENTRY} is {hashlib.sha256(bundled).hexdigest()}, the attested member is {hashlib.sha256(attested).hexdigest()}", file=sys.stderr)
    sys.exit(1)
print(f"verify-bundled-exe: {ENTRY} is byte for byte the attested member, sha256 {hashlib.sha256(attested).hexdigest()}")
PYTHON
