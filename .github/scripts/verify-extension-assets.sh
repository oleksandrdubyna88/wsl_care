#!/usr/bin/env bash
# The extension release's asset set (E5.S3, plan §15g M5): a folder holds EXACTLY `wsl-care-<version>.vsix` and
# `wsl-care-<version>.vsix.sha256`, and the .sha256 is one line `<sha-256>  wsl-care-<version>.vsix` that matches the file.
# release-extension.yml runs it on what the build produced, on what it uploaded to the draft (downloaded back), before
# the Marketplace publishes that file, and before the draft goes public — so what goes public is what was checked.
#
#   verify-extension-assets.sh <version> <dir>
#
# Exit 0 and one line saying so; any refusal exits 1 naming it.
set -euo pipefail

[ "$#" -eq 2 ] || { echo "usage: verify-extension-assets.sh <version> <dir>" >&2; exit 2; }
version="$1"
dir="$2"

refuse() {
  echo "::error::extension assets: $*"
  exit 1
}

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || refuse "'$version' is not an x.y.z version"
[ -d "$dir" ] || refuse "$dir is not a folder"

vsix="wsl-care-$version.vsix"
expected="$(printf '%s\n%s\n' "$vsix" "$vsix.sha256")"
present="$(cd "$dir" && find . -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)"
[ "$present" = "$expected" ] || refuse "$dir holds [$(printf '%s' "$present" | tr '\n' ' ')], expected exactly [$vsix $vsix.sha256]"

line="$(cat "$dir/$vsix.sha256")"
[[ "$line" =~ ^[0-9a-f]{64}\ \ wsl-care-[0-9.]+\.vsix$ ]] || refuse "$vsix.sha256 is not one '<sha-256>  $vsix' line"
[ "${line#*  }" = "$vsix" ] || refuse "$vsix.sha256 names '${line#*  }', not $vsix"
(cd "$dir" && sha256sum --check --status "$vsix.sha256") || refuse "$vsix does not match its .sha256"

echo "extension assets: $vsix and its .sha256, nothing else, the checksum matches"
