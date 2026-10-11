#!/usr/bin/env bash
# The extension release's asset set (E5.S3, plan §15g M5): a folder holds EXACTLY the package and its .sha256, and the .sha256
# is one line `<sha-256>  <package>` that matches the file. The package's name comes from lib/extension-package.sh's
# `vsix_name` — since E7.S5a the TARGETED `ai-os-care-win32-x64-<version>.vsix`; the universal `ai-os-care-<version>.vsix` of
# the releases before it is REFUSED (a release from this one on is win32-x64 only). release-extension.yml runs it on what the
# build produced, on what it uploaded to the draft (downloaded back), before the Marketplace publishes that file, and before
# the draft goes public — so what goes public is what was checked.
#
#   verify-extension-assets.sh <version> <dir>
#
# Exit 0 and one line saying so; any refusal exits 1 naming it.
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: verify-extension-assets.sh <version> <dir>" >&2; exit 2; }
version="$1"
dir="$2"

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source-path=SCRIPTDIR source=lib/extension-package.sh
. "$here/lib/extension-package.sh"

refuse() {
  echo "::error::extension assets: $*"
  exit 1
}

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || refuse "'$version' is not an x.y.z version"
[ -d "$dir" ] || refuse "$dir is not a folder"

vsix="$(vsix_name "$version")"
universal="$(vsix_universal_name "$version")"
[ ! -e "$dir/$universal" ] || refuse "$dir holds $universal — the universal package name; from E7.S5a on the package is $vsix (win32-x64 only)"
expected="$(printf '%s\n%s\n' "$vsix" "$vsix.sha256")"
present="$(cd "$dir" && find . -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
[ "$present" = "$expected" ] || refuse "$dir holds [$(printf '%s' "$present" | tr '\n' ' ')], expected exactly [$vsix $vsix.sha256]"

line="$(cat "$dir/$vsix.sha256")"
shape="^[0-9a-f]{64}  ${EXTENSION_NAME}-[A-Za-z0-9.-]+\\.vsix\$"
[[ "$line" =~ $shape ]] || refuse "$vsix.sha256 is not one '<sha-256>  $vsix' line"
[ "${line#*  }" = "$vsix" ] || refuse "$vsix.sha256 names '${line#*  }', not $vsix"
(cd "$dir" && sha256sum --check --status "$vsix.sha256") || refuse "$vsix does not match its .sha256"

echo "extension assets: $vsix and its .sha256, nothing else, the checksum matches"
