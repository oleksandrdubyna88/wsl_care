#!/usr/bin/env bash
# Packs ONE daemon release archive and its .sha256 (plan §9, §15e #1; E4.S2), the way release.yml does — release.yml
# calls exactly this; so does ci-daemon.yml, on every pull request, on all three runners (Git Bash on Windows), with the
# published AOT binary of its leg; and the scenario suite runs it against a stub binary (PackageFlows on Linux,
# PackagePathFlows on every OS, the Windows zip wherever 7-Zip is on PATH).
#
#   package-daemon.sh <version> <rid> <publish-dir> <out-dir>
#
# Linux RIDs -> <out-dir>/wsl-care-<version>-<rid>.tar.gz holding exactly
#   wsl-care-<version>-<rid>/wsl-care                         (0755, the AOT binary from <publish-dir>)
#   wsl-care-<version>-<rid>/systemd/wsl-care.service        (0644, every file of src_daemon/systemd/)
#   wsl-care-<version>-<rid>/systemd/wsl-care.timer
#   wsl-care-<version>-<rid>/systemd/wsl-care-events.service
#   wsl-care-<version>-<rid>/systemd/wsl-care-act@.service    (E6.S1: the detached run's template unit)
#   wsl-care-<version>-<rid>/systemd/wsl-care-watch.service  (E14 S2b: the watch's run)
#   wsl-care-<version>-<rid>/systemd/wsl-care-watch.timer
#   wsl-care-<version>-<rid>/config/machine.json             (0644, src_daemon/config/ — the EMPTY machine layer)
# plus the three folders: regular files and folders only (install.sh refuses a link or a special file), owner 0:0,
# names sorted, gzip without a timestamp.
#
# win-x64 -> <out-dir>/wsl-care-<version>-win-x64.zip holding exactly wsl-care-<version>-win-x64/wsl-care.exe: the
# Windows probe ships no systemd units and no distro machine layer — neither means anything on the host (the
# extension bundles the exe, E5; the Windows daemon's own install is E11). Made with 7-Zip, which every GitHub
# Windows and Ubuntu image carries (the family credential-store repository's release legs use the same command).
#
# Beside each archive, <archive>.sha256 in sha256sum's own format — "<64 hex>  <archive name>" — which install.sh reads
# (its first field) and `sha256sum -c` checks as it is.
#
# Prints the archive's path as its LAST line of stdout, spelled for the CALLER: built from <out-dir> exactly as given
# (a relative one stays relative to the caller's working directory), and through `cygpath -m` where that exists (Git
# Bash). The workflows hand this path to attest-build-provenance and upload-artifact — Windows programs on a Windows
# runner, which read an MSYS path such as /d/a/… as D:\d\a\… and find nothing (E4 review B1: the win-x64 release leg
# would have failed at the attestation). Exit 2 on a bad argument, 1 on a failed step.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/daemon-assets.sh
. "$here/lib/daemon-assets.sh"
repo="$(cd "$here/../.." && pwd)"

usage() {
  echo "package-daemon.sh: $*" >&2
  echo "usage: package-daemon.sh <version> <rid> <publish-dir> <out-dir>" >&2
  exit 2
}

fail() {
  echo "package-daemon.sh: $*" >&2
  exit 1
}

[ "$#" -eq 4 ] || usage "expected 4 arguments, got $#"
version="$1"
rid="$2"
publish="$3"
out="$4"
out_as_given="${4%/}"

[[ "$version" =~ $DAEMON_VERSION_PATTERN ]] || usage "not a release version: $version"
archive="$(daemon_archive_name "$version" "$rid")" || usage "not a RID a daemon release ships: $rid (ships: $DAEMON_RIDS)"
folder="$(daemon_archive_folder "$version" "$rid")"

case "$rid" in
  win-*) binary="wsl-care.exe" ;;
  *) binary="wsl-care" ;;
esac
[ -f "$publish/$binary" ] && [ ! -L "$publish/$binary" ] || fail "no published binary at $publish/$binary"

mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

# Copied with fixed modes by `install`, never linked: a member is always a regular file whatever the source was.
install -d -m 0755 "$stage/$folder"
install -m 0755 "$publish/$binary" "$stage/$folder/$binary"

case "$rid" in
  win-*)
    command -v 7z > /dev/null || fail "7z is not on PATH: the Windows archive is a zip made with 7-Zip"
    rm -f "$out/$archive"
    (cd "$stage" && 7z a -tzip -bso0 -bsp0 "$out/$archive" "$folder") || fail "7z could not write $archive"
    ;;
  *)
    install -d -m 0755 "$stage/$folder/systemd" "$stage/$folder/config"
    # The folder's files, not a third list of their names: ShippedFilesTests holds install.sh's UNITS equal to them.
    for unit in "$repo"/src_daemon/systemd/*; do
      install -m 0644 "$unit" "$stage/$folder/systemd/${unit##*/}"
    done
    install -m 0644 "$repo/src_daemon/config/machine.json" "$stage/$folder/config/machine.json"
    tar --sort=name --owner=0 --group=0 --numeric-owner --format=ustar -C "$stage" -cf - "$folder" \
      | gzip -9n > "$out/$archive" || fail "tar could not write $archive"
    ;;
esac

# The line is written here, not by `sha256sum > file`: Git Bash's sha256sum may mark binary mode with "*" before the
# name, and the format install.sh and verify-release-assets.sh read is the two-space one, on every runner.
hash="$(cd "$out" && sha256sum "$archive" | cut -d' ' -f1)" || fail "could not hash $archive"
hash="${hash#\\}"
printf '%s  %s\n' "$hash" "$archive" > "$out/$archive.sha256" || fail "could not write $archive.sha256"
printed="$out_as_given/$archive"
if command -v cygpath > /dev/null 2>&1; then
  printed="$(cygpath -m "$printed")" || fail "cygpath could not spell $printed for a Windows program"
fi
printf '%s\n' "$printed"
