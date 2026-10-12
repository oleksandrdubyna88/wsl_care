#!/usr/bin/env bash
# The ONE fetch of the Windows daemon the extension bundles (E7.S5a, research/PLAN_bundle_windows_binary.md §3.2): the release
# build, pull-request CI, the attest job's re-derivation and a developer (`npm run fetch:windows-daemon`) all run it.
#
#   fetch-windows-daemon.sh <version> <out-dir>       ->  <out-dir>/bin/wsl-care.exe and <out-dir>/bin/wsl-care.exe.sha256
#
# In order, and any failure exits 1 and leaves NOTHING in <out-dir>/bin (a file a previous run left there is removed):
#   1. `gh release download daemon-v<version>` of exactly the win-x64 zip and its .sha256 — up to WSL_CARE_FETCH_ATTEMPTS times
#      (3; 1–10), waiting k x WSL_CARE_FETCH_BACKOFF_SECONDS (10; 0–300) before attempt k + 1, each attempt ended at
#      WSL_CARE_FETCH_ATTEMPT_SECONDS (300; 1–3600) and killed WSL_CARE_FETCH_KILL_GRACE_SECONDS (5; 1–60) later, every failed
#      attempt said on stderr (lib/retry.sh); a setting out of range is a usage error (2) before anything is asked. Only the
#      download is retried: a wrong answer from the steps below is not transient and stays a refusal;
#   2. verify-release-assets.sh <version> <download> win-x64 — exactly those two files, the .sha256 one
#      `<sha-256>  <zip>` line naming that zip, and the checksum matching;
#   3. `gh attestation verify` with the EXACT signer identity `release.yml@refs/tags/daemon-v<version>`, the source ref of
#      that tag and `--deny-self-hosted-runners` — never a prefix (module_tests O4);
#   4. python's zipfile reads the zip: exactly ONE file entry, named exactly `wsl-care-<version>-win-x64/wsl-care.exe` (a
#      directory entry of exactly that folder beside it is allowed: 7z writes one); any other entry, a symlink, an absolute,
#      `..`, backslash or drive name, or a case variant is refused. The member is read by its exact name — nothing is ever
#      extracted by an archive-supplied path — and written with its SHA-256 beside it.
#
# GH_TOKEN comes from the caller's environment (a workflow step's own `env:`, never job-wide). On Windows (Git for Windows'
# bash), the paths handed to gh and python — Windows programs — are spelt `cygpath -m` (E4 B1).
# Exit 0 and one line naming the member and its digest; 1 a refusal; 2 a bad argument.
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: fetch-windows-daemon.sh <version> <out-dir>" >&2; exit 2; }
version="$1"
out="$2"
bin="$out/bin"
exe="$bin/wsl-care.exe"
# First of all, before even the version is judged: a refused run never leaves an older pair for a local package to take
# (coai code round — a mistyped version used to exit here with the previous exe and its digest still in bin/).
rm -f "$exe" "$exe.sha256"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "fetch-windows-daemon.sh: not an x.y.z daemon version: $version" >&2; exit 2; }

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/daemon-assets.sh
. "$here/lib/daemon-assets.sh"
# shellcheck source-path=SCRIPTDIR source=lib/retry.sh
. "$here/lib/retry.sh"

# The download's bounded retry (GitHub once answered "release not found" for a release that exists): the attempts and the
# backoff step are settings of the caller's environment, checked before anything is asked.
attempts="$(retry_setting WSL_CARE_FETCH_ATTEMPTS 3 1 10)"
backoff="$(retry_setting WSL_CARE_FETCH_BACKOFF_SECONDS 10 0 300)"
# Each attempt is bounded, as wait-marketplace-served.sh bounds its own: a stalled gh is ended at the attempt limit (exit 124,
# as timeout reports it) and KILLed the grace later if it ignores TERM (gh starts no child for a download).
attempt_seconds="$(retry_setting WSL_CARE_FETCH_ATTEMPT_SECONDS 300 1 3600)"
kill_grace="$(retry_setting WSL_CARE_FETCH_KILL_GRACE_SECONDS 5 1 60)"

# The repository and the signer are constants: an identity taken from the environment could be pointed elsewhere.
readonly REPO="oleksandrdubyna88/wsl_care"
tag="daemon-v$version"
zip="$(daemon_archive_name "$version" win-x64)"
member="$(daemon_archive_folder "$version" win-x64)/wsl-care.exe"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

refuse() {
  rm -f "$exe" "$exe.sha256"
  echo "::error::fetch-windows-daemon: $*"
  exit 1
}

# native and python_cmd: one copy for every script that reads a zip.
# shellcheck source-path=SCRIPTDIR source=lib/windows-tools.sh
. "$here/lib/windows-tools.sh"

# One attempt: a fresh, empty download folder each time, so a partial download never meets the asset check.
download_assets() {
  rm -rf "$work/download" && mkdir -p "$work/download" \
    && timeout -k "$kill_grace" "$attempt_seconds" gh release download "$tag" --repo "$REPO" -p "$zip" -p "$zip.sha256" --dir "$(native "$work/download")"
}

with_retries "$attempts" "$backoff" download "(limit $attempt_seconds s)" download_assets \
  || refuse "could not download $zip and its .sha256 from $tag after $attempts attempts (is daemon $version published with its win-x64 asset?)"
bash "$here/verify-release-assets.sh" "$version" "$work/download" win-x64 \
  || refuse "the download of $tag is not exactly $zip and its matching .sha256"
gh attestation verify "$(native "$work/download/$zip")" --repo "$REPO" \
  --cert-identity "https://github.com/$REPO/.github/workflows/release.yml@refs/tags/$tag" \
  --source-ref "refs/tags/$tag" --deny-self-hosted-runners \
  || refuse "$zip carries no attestation by release.yml at $tag"

py="$(python_cmd)" || refuse "no python with zipfile here (python3 or python)"
digest="$("$py" -I -B - "$(native "$work/download/$zip")" "$member" "$(native "$work/wsl-care.exe")" "$(native "$here/lib")" <<'PYTHON'
import hashlib, stat, sys, zipfile

archive_path, member, target = sys.argv[1], sys.argv[2], sys.argv[3]
sys.path.insert(0, sys.argv[4])
from zip_entries import unsafe  # the ONE entry-name rule, shared with verify-bundled-exe.sh

folder = member.split("/")[0] + "/"

def refuse(why):
    print(why, file=sys.stderr)
    sys.exit(1)

try:
    archive = zipfile.ZipFile(archive_path)
except Exception as error:
    refuse(f"not a readable zip: {error}")

files = []
for info in archive.infolist():
    # The name AS STORED: zipfile turns a backslash into "/" when it reads on Windows, so `filename` would differ by OS.
    name = info.orig_filename
    if unsafe(name):
        refuse(f"an unsafe entry name: {name!r}")
    if stat.S_ISLNK(info.external_attr >> 16):
        refuse(f"a symlink entry: {name!r}")
    if info.is_dir():
        if name != folder:
            refuse(f"a directory entry other than {folder!r}: {name!r}")
        continue
    files.append(name)

if files != [member]:
    refuse(f"the zip must hold exactly one file, {member!r}; it holds {files!r}")

data = archive.read(member)
with open(target, "wb") as handle:
    handle.write(data)
print(hashlib.sha256(data).hexdigest())
PYTHON
)" || refuse "$zip is not exactly one $member"

printf '%s  wsl-care.exe\n' "$digest" > "$work/wsl-care.exe.sha256" || refuse "could not write the digest of $member"
{ mkdir -p "$bin" && mv "$work/wsl-care.exe" "$exe" && mv "$work/wsl-care.exe.sha256" "$exe.sha256"; } \
  || refuse "could not place $member and its digest in $bin"
echo "fetch-windows-daemon: $member of the attested $tag, sha256 $digest"
