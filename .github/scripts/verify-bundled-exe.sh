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
#   2. python's zipfile reads `extension/bin/wsl-care.exe` from the package — exactly one entry by that name;
#   3. the two are compared byte for byte.
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

native() {
  if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi
}

python_cmd() {
  local candidate
  for candidate in python3 python; do
    if "$candidate" -c 'import zipfile' > /dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

bash "$here/fetch-windows-daemon.sh" "$version" "$work/attested" \
  || refuse "the attested daemon-v$version member could not be fetched and verified — nothing is signed"
py="$(python_cmd)" || refuse "no python with zipfile here (python3 or python)"
"$py" - "$(native "$vsix")" "$(native "$work/attested/bin/wsl-care.exe")" <<'PYTHON' || refuse "$vsix does not carry exactly the attested daemon-v$version member — nothing is signed"
import hashlib, sys, zipfile

package, attested_path = sys.argv[1], sys.argv[2]
ENTRY = "extension/bin/wsl-care.exe"

try:
    archive = zipfile.ZipFile(package)
except Exception as error:
    print(f"not a readable package: {error}", file=sys.stderr)
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
